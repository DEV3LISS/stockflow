# Stockflow — Requirements

Backend for a small online shop whose hardest problem is **inventory under concurrency**: never sell what is not in
stock, never lose stock to abandoned checkouts, and never charge for an order twice.

## 1. Context

A shop with a few thousand products. Most of the time traffic is calm; during a promotion ("flash sale")
hundreds of people try to buy the same few items within seconds. Typical failures in naive shops:

- **overselling** — two buyers both get the last unit;
- **stuck stock** — items reserved by checkouts that were never paid stay unavailable forever;
- **double charging / double fulfilment** — the payment provider sends the same notification twice;
- **slow catalog** under load because every page hits the database.

## 2. Roles

| Role | Can |
|---|---|
| `Customer` | Register, browse the catalog, manage own cart, place and pay own orders, see own orders |
| `Admin` | Manage catalog and prices, receive and adjust stock, see all orders, cancel/ship orders, reports |

Anonymous visitors can browse the catalog.

## 3. Functional requirements

### FR-1 Accounts
- Customers self-register (email + password, rate-limited); admins are created by an admin or the CLI.
- JWT access + rotating refresh tokens (same design as Torque CRM).

### FR-2 Catalog
- Categories; products with SKU (unique), name, description, price, active flag.
- Public listing with category filter, text search, paging, sorting by price/name; product details with current availability.
- Changing a price never changes existing orders (price snapshot in order lines).

### FR-3 Inventory
- Per product: `on_hand` (physically in the warehouse) and `reserved` (held by unpaid orders). `available = on_hand − reserved`.
- Admin **receives** stock (+) and makes **adjustments** (±, with a reason: damaged, inventory count).
- Every change is written to an append-only **inventory ledger** (movement type, quantity, reference, who, when),
  and the current level must always equal the sum of the ledger.
- Invariants enforced by the database: `on_hand ≥ 0`, `reserved ≥ 0`, `reserved ≤ on_hand`.

### FR-4 Cart
- One cart per customer; add/update/remove items; quantity 1–20 per line.
- The cart does **not** reserve stock (that would let bots lock up inventory); availability is shown, not promised.

### FR-5 Checkout and reservation
- `POST /orders` (with `Idempotency-Key`) turns the cart into an order **atomically**: either every line is reserved, or nothing is and the customer gets 409 with the lines that are short.
- The order is `PendingPayment` and holds the reservation for **15 minutes**.
- **The last unit is sold exactly once** no matter how many customers check out at the same moment.

### FR-6 Payment (provider simulator + webhook)
- The API creates a payment at the (simulated) provider and returns a payment URL/id.
- The provider calls back `POST /webhooks/payments` with an HMAC-SHA256 signature.
- Webhooks are verified, **idempotent** (same event twice → processed once) and **order-tolerant** (a late "failed" after "succeeded" must not cancel a paid order).
- On success: order `Paid`, reservation converted into a sale (`on_hand −= q`, `reserved −= q`, ledger entry).

### FR-7 Expiry
- A background job cancels `PendingPayment` orders whose reservation expired and releases the stock.
- Safe to run on several instances at once; a payment arriving just as the order expires must not produce a paid-but-cancelled order.

### FR-8 Order lifecycle
- `PendingPayment → Paid → Shipped`; `PendingPayment → Cancelled | Expired`; admin can cancel a paid order (refund is out of scope, recorded as `Cancelled` + stock returned).

### FR-9 Reports (Admin)
- Sales by day, best sellers, low-stock list, reservations currently held.

## 4. Non-functional requirements

As Torque CRM (ProblemDetails, validation, structured logs, health checks, OpenTelemetry, Docker Compose,
Testcontainers, CI), plus:

| Area | Requirement |
|---|---|
| Concurrency | Proven by tests and a load test: 100 parallel buyers for 1 unit → exactly 1 order |
| Performance | Catalog p95 < 50 ms at 200 req/s on 10 000 products (measured; cache only if it is needed to reach it) |
| Resilience | Redis outage must not break the shop (fall back to the database) |
| Security | Webhook signature verification with constant-time comparison; customers can access only their own carts/orders (IDOR tests) |

## 5. Out of scope

Real payment provider, shipping/delivery integration, multiple warehouses, coupons, refunds of money, frontend.
