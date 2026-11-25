# Security

Baseline (JWT with rotating refresh tokens, PBKDF2 passwords, rate limiting, security headers, body-size limit,
ProblemDetails without internals, secrets from environment, vulnerable-dependency CI check) is shared with
Torque CRM — see its [security.md](https://github.com/DEV3LISS/torque-crm/blob/main/docs/security.md). Shop-specific:

| Risk | Protection | Test |
|---|---|---|
| IDOR on orders | Customers see only own orders; others' ids → **404** (no existence leak); `customerId` filter ignored for customers | `Customers_cannot_see_or_cancel_each_others_orders` |
| IDOR on carts | No cart id in the API at all (`/cart` = current user's cart) | by design |
| Privilege escalation at sign-up | `/auth/register` always creates `Customer`; extra `role` field ignored | `Registration_cannot_choose_the_admin_role` |
| Forged payment notifications | HMAC-SHA256 over the raw body, constant-time compare | `Forged_or_replayed_webhooks_are_rejected`, `WebhookSignatureTests` |
| Replay of a captured webhook | Signed timestamp, 5-minute tolerance + event-id dedup | same |
| Price manipulation | Prices are never taken from the client: order lines snapshot the DB price at checkout | `Checkout_reserves_stock…` |
| Inventory abuse by bots | Carts do not reserve stock; checkout requires an account and is idempotent | ADR-003 |
| SQL injection | Parameterised LINQ and `FormattableString` SQL only | `Sql_injection_in_search_is_just_text` |
| Payment simulator in production | Disabled unless `PaymentSimulator:Enabled=true` | config |
