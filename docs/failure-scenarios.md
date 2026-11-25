# Failure scenarios

| # | Scenario | Behaviour | Verified by |
|---|---|---|---|
| 1 | 100 buyers, 1 unit, same millisecond | exactly one order; 99 × 409 `outOfStock` | `Hundred_buyers_for_the_last_unit…` (×3 strategies), k6 flash sale |
| 2 | Multi-line cart, one line short | nothing reserved (transaction rolled back), 409 lists the SKU | `Multi_line_checkout_is_all_or_nothing` |
| 3 | Client retries "Place order" (timeout, double click) | same order returned (`Idempotency-Key`) | `Retried_checkout…`, E2E |
| 4 | Provider delivers the same webhook 3 times | applied once, then `Duplicate` | `Same_webhook_delivered_three_times…`, simulator `deliveries=2` |
| 5 | `failed` event arrives after `succeeded` | ignored (terminal state) | `Late_failed_event_does_not_undo_a_success` |
| 6 | Forged or replayed webhook | 401, nothing changes | `Forged_or_replayed_webhooks_are_rejected`, unit tests |
| 7 | Buyer never pays | after 15 min the expiry job releases stock | `Unpaid_order_expires…` |
| 8 | Payment arrives after expiry, unit already resold | payment `NeedsRefund`, order stays `Expired`, no double sale | `Payment_arriving_after_expiry…` |
| 9 | Payment and expiry at the same instant | order row lock: exactly one wins, stock consistent (10 rounds) | `Payment_and_expiry_racing…` |
| 10 | Several instances run the expiry worker | `SKIP LOCKED`: each order expired once | `Expiry_workers_on_several_instances…` |
| 11 | Expiry worker stopped | `/health/status` → Degraded ("reservations overdue") | `ReservationExpiryHealthCheck` |
| 12 | **Redis down** | catalog falls back to PostgreSQL, still 200 | `Catalog_keeps_working_when_redis_is_down` |
| 13 | Cached availability is stale | display only — checkout re-checks in the DB, cannot oversell | `Stale_cached_availability_can_never_oversell` |
| 14 | PostgreSQL down | ready = 503, API 503 + `Retry-After`, recovers without restart | `DatabaseDownTests` (same mechanism as Torque CRM, chaos-tested there) |
| 15 | Admin reduces stock below reservations | 422 — cannot take units promised to unpaid orders | `Admin_cannot_adjust_stock_below_what_is_reserved` |

After every concurrency test the database invariants are asserted: `on_hand = Σ ledger` and
`reserved = Σ PendingPayment order lines`.
