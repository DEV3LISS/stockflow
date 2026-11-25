# Architecture Decision Records

Format: **Problem → Options → Decision → Arguments → Drawbacks → When to choose an alternative.**
The layered architecture, error handling, auth and test setup are inherited from
[Torque CRM](https://github.com/DEV3LISS/torque-crm/blob/main/docs/decisions.md) (ADR-001…008 there) and not repeated.

| # | Decision |
|---|---|
| 001 | Stock as `on_hand` + `reserved` with database invariants |
| 002 | Append-only inventory ledger |
| 003 | Reserve at checkout, not in the cart; 15-minute hold |
| 004 | Atomic conditional `UPDATE` for reservations (vs. `FOR UPDATE`, optimistic) |
| 005 | Payment webhooks: HMAC signature, exactly-once, terminal states |
| 006 | HybridCache (memory + Redis) for catalog pages — only after measuring |
| 007 | Expiry worker with `FOR UPDATE SKIP LOCKED`; late payments flagged for refund |
| 008 | 404 instead of 403 for other customers' orders |

## ADR-001 Stock as `on_hand` + `reserved` with database invariants

**Problem.** "How many can I sell?" must be exact while hundreds of checkouts and payments change stock concurrently.

**Options.** (a) One `quantity` column decremented at checkout; (b) compute availability from orders on every read; (c) two counters `on_hand` (physically present) and `reserved` (held by unpaid orders), `available = on_hand − reserved`.

**Decision.** (c), with `CHECK (on_hand >= 0)` and `CHECK (reserved >= 0 AND reserved <= on_hand)` in PostgreSQL.

**Arguments.** Unpaid orders can expire without corrupting the physical count; warehouse staff see both numbers; availability is one row read. The CHECKs make overselling impossible even for a buggy code path or a manual SQL script.

**Drawbacks.** Two counters must be kept in sync with orders — verified by an invariant check after every concurrency test (`reserved` = sum of `PendingPayment` order lines).

**Alternatives.** (a) for shops without online payment delays; (b) for tiny volumes.

## ADR-002 Append-only inventory ledger

**Problem.** "Where did these 5 units go?" — stock numbers alone cannot answer, and a wrong number cannot be audited or rebuilt.

**Decision.** Every change of `on_hand` writes an `inventory_movements` row (Receipt, Adjustment with reason, Sale, Return) in the same transaction. Invariant: `on_hand = Σ movements` — asserted in tests.

**Arguments.** Accounting-style traceability; adjustments require a reason and an author; stock can be recomputed.

**Drawbacks.** More writes; reservations are deliberately *not* ledger entries (they do not move goods) — `reserved` is derivable from orders.

**Alternatives.** Full event sourcing of inventory (stock = fold of events) when history *is* the product (e.g. regulated goods).

## ADR-003 Reserve at checkout, not in the cart; 15-minute hold

**Problem.** When is stock taken away from other buyers?

**Options.** (a) When added to cart; (b) at checkout with a time limit; (c) only at payment.

**Decision.** (b): checkout reserves all lines atomically (all or nothing) for 15 minutes; payment converts the reservation into a sale; an expiry job releases unpaid holds.

**Arguments.** (a) lets anyone — or a bot — lock inventory by filling carts. (c) lets two people pay for the last unit, one of them must then be refunded. (b) guarantees that whoever reaches the payment page can buy.

**Drawbacks.** Units are unavailable for up to 15 minutes even if the buyer leaves; needs a reliable expiry job (ADR-007) and a policy for payments that arrive late.

## ADR-004 Atomic conditional `UPDATE` for reservations

**Problem.** In a flash sale hundreds of transactions compete for the same `stock_items` row. Naive "SELECT available → if enough → UPDATE" oversells: all readers see the same last unit.

**Options (all three implemented behind `IStockReservations`, switchable via `Inventory:ReservationStrategy`, and measured):**
1. **Atomic** — `UPDATE stock_items SET reserved = reserved + :q WHERE product_id = :id AND on_hand - reserved >= :q`; 0 rows updated = not enough.
2. **Pessimistic** — `SELECT … FOR UPDATE`, check in C#, `UPDATE`.
3. **Optimistic** — read with version (`xmin`), `UPDATE … WHERE xmin = :old`, retry up to 10 times.

**Results** (300 simultaneous buyers, 100 units, see [performance](performance.md)): all three sold exactly 100, zero errors; checkout p95 563 / 584 / 599 ms.

**Decision.** Atomic as default.

**Arguments.** Check and write are one statement: no round-trips while holding the row lock, no retry loop, simplest code. Under contention PostgreSQL re-evaluates the `WHERE` on the newest row version, so the last unit is taken once. Multi-line orders lock rows in product-id order → no deadlocks between two carts.

**Drawbacks.** The rule lives in SQL text (tested, not compiled). Pessimistic holds locks across round-trips; optimistic wastes work on retries and can give up ("very high demand, try again") — the gap grows with latency between app and DB.

**Alternatives.** Pessimistic when the decision needs complex C# logic over several rows; optimistic when conflicts are rare (e.g. admin edits — used for receipts/adjustments). For extreme sales: a queue/token bucket in front of checkout or Redis `DECR` pre-allocation with DB reconciliation.

## ADR-005 Payment webhooks: HMAC signature, exactly-once, terminal states

**Problem.** The provider notifies us over HTTP: anyone can call the URL; deliveries are retried (duplicates); events can arrive out of order.

**Decision.**
- Signature header `t=<unix>,v1=HMAC-SHA256(secret, "<t>.<raw body>")`, verified on the raw body with a constant-time comparison and a 5-minute timestamp tolerance (replay protection).
- `processed_webhook_events(event_id PK)` written in the same transaction as the effect → each event applied once.
- Payment statuses are terminal (`Succeeded`, `Failed`, `NeedsRefund` never change), so a late `failed` cannot undo `succeeded`.
- The order row is locked while applying, serialising with the expiry job.

**Arguments.** Mirrors how Stripe-style providers work; tested with forged, replayed, duplicate and out-of-order events.

**Drawbacks.** The secret must be rotated carefully (support two secrets during rotation — not implemented). The event table grows (prune after the provider's retry window).

## ADR-006 HybridCache (memory + Redis) for catalog pages — only after measuring

**Problem.** Is a cache needed, and where?

**Measurements** (10 010 products, k6 open model): without cache p95 = 10 ms at 200 req/s, 5 ms at 600 req/s — **no cache needed for normal traffic**. At 3 000 req/s the API served only ~2 430 req/s with p95 = 207 ms; PostgreSQL used ~620 % CPU, i.e. the database was the bottleneck.

**Options.** (a) No cache; (b) in-process `IMemoryCache`; (c) Redis only; (d) `HybridCache`: in-process L1 + Redis L2 + stampede protection; (e) HTTP caching/CDN.

**Decision.** (d) for public category pages and product pages: 10 s in Redis, 5 s in process, tag-based invalidation on any product change. Search and admin views are not cached. Checkout never reads the cache.

**Result.** 3 000 req/s served fully, p95 207 → **7.5 ms**, DB CPU ~620 % → ~190 %.

**Arguments.** Removes the measured bottleneck; L1 avoids a network hop for hot keys; single-flight protects the DB from a stampede when a popular key expires; Redis shares entries across instances. Availability shown may be ≤ 10 s stale — harmless because the reservation is an atomic DB check (test: stale "1 available" → checkout still 409).

**Drawbacks.** Another moving part; staleness window; L1 of other instances is not invalidated instantly (5 s).

**Resilience.** Redis down → HybridCache falls back to the database (test with an unreachable Redis: catalog still 200).

**Alternatives.** (e) CDN/HTTP cache for anonymous catalog traffic at internet scale; (b) for a single instance.

## ADR-007 Expiry worker with `FOR UPDATE SKIP LOCKED`; late payments flagged for refund

**Problem.** Release unpaid reservations reliably, on several instances, without racing with a payment that arrives at the deadline.

**Decision.** A `BackgroundService` per instance runs every 10 s: in one transaction it locks up to 50 due orders with `FOR UPDATE SKIP LOCKED`, releases stock, marks them `Expired`. The payment webhook locks the same order row, so exactly one of them wins. A payment for an already expired/cancelled order is **not** applied (the units may be sold to someone else): the payment becomes `NeedsRefund` and is surfaced in `/health/status`.

**Arguments.** No scheduler infrastructure; horizontally safe; tested: 4 parallel workers expire 15 orders exactly once; 10 rounds of payment-vs-expiry races always end consistent.

**Drawbacks.** Polling (≤ 10 s extra hold); refunds need a human or a refund API (out of scope).

**Alternatives.** Delayed messages in a broker; Hangfire/Quartz for richer scheduling.

## ADR-008 404 instead of 403 for other customers' orders

**Problem.** A customer requests `/orders/{id}` of someone else.

**Decision.** Respond **404**, and make cart endpoints id-less (`/cart` = "my cart").

**Arguments.** 403 confirms the id exists (lets attackers enumerate orders); an id-less cart makes IDOR impossible by design. Customers' `customerId` filter is ignored.

**Drawbacks.** Slightly confusing for legitimate debugging (admins see everything, so this does not affect support).
