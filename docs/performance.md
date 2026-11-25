# Performance

Measured on a MacBook (Apple Silicon, 10 CPUs for Docker), production image via `docker compose`, PostgreSQL 17,
Redis 7.4. Reproduce with the scripts in [`loadtests/`](../loadtests).

## 1. Catalog under load — is a cache needed?

Data: 10 010 products in 23 categories ([`seed-catalog.sql`](../loadtests/seed-catalog.sql)).
Load: [`catalog.js`](../loadtests/catalog.js), constant arrival rate, 30 s, 60 % category pages / 25 % product pages / 15 % search.

| Rate | Cache | Served | p95 | Notes |
|---|---|---|---|---|
| 200 req/s | none | 200 req/s | 10.1 ms | target p95 < 50 ms met without cache |
| 600 req/s | none | 600 req/s | 5.4 ms | |
| 1 500 req/s | none | 1 496 req/s | 7.9 ms | |
| 3 000 req/s | none | **2 434 req/s** (16 % dropped) | **207 ms** | PostgreSQL ~620 % CPU, API ~190 % → **DB is the bottleneck** |
| 600 req/s | HybridCache + Redis | 596 req/s | 6.8 ms (median 1.1 ms) | DB ~35 % CPU |
| 3 000 req/s | HybridCache + Redis | **2 996 req/s** | **7.5 ms** | DB ~190 % CPU (uncached search) |

Conclusion: the cache is **not** needed for normal traffic and was added for peak traffic only, after the
measurement showed the database saturating (ADR-006).

## 2. Flash sale — three reservation strategies

Load: [`flash-sale.js`](../loadtests/flash-sale.js) — 300 customers (registered in setup, each with the item in the cart)
press "Place order" at the same moment for a product with 100 units. Run once per strategy
(`RESERVATION_STRATEGY=… docker compose up -d api`).

| Strategy | Orders | Out of stock (409) | Errors | Checkout p95 | Stock after |
|---|---|---|---|---|---|
| Atomic `UPDATE … WHERE available >= q` | 100 | 200 | 0 | **563 ms** | on_hand 100, reserved 100 |
| `SELECT … FOR UPDATE` | 100 | 200 | 0 | 584 ms | on_hand 100, reserved 100 |
| Optimistic (`xmin` + retries) | 100 | 200 | 0 | 599 ms | on_hand 100, reserved 100 |

All three are **correct**. The absolute latency (~0.5 s) is dominated not by stock locking but by queueing: 300 requests
arrive within a millisecond while the API has a pool of 50 database connections. The atomic statement is the
fastest and simplest; the gap would grow with network latency between API and DB (locks held across round-trips,
retries). Integration tests run the same race (100 buyers, 1 unit) for every strategy on each build.

## Next steps

- Queue checkouts per product during announced sales (fair ordering, protects the pool).
- Keyset pagination for deep catalog pages; materialised `available` in a read model if search grows.
