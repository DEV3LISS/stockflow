-- 10 000 products in 20 categories with stock and a matching ledger entry (keeps the invariant on_hand = sum(ledger)).
-- Run: docker compose exec -T db psql -U stockflow -d stockflow < loadtests/seed-catalog.sql
\timing on
BEGIN;
INSERT INTO categories (id, name, slug, created_at, updated_at)
SELECT gen_random_uuid(), 'Bulk category ' || c, 'bulk-' || c, now(), now() FROM generate_series(1, 20) c;

INSERT INTO products (id, sku, name, description, price, category_id, is_active, created_at, updated_at)
SELECT gen_random_uuid(), 'BULK-' || lpad(i::text, 6, '0'),
       (ARRAY['Phone','Laptop','Headphones','Speaker','Watch','Tablet','Camera','Monitor','Keyboard','Mouse'])[1 + i % 10]
         || ' model ' || i,
       'Bulk demo product ' || i, (100 + (i * 37) % 90000)::numeric(12,2),
       (SELECT id FROM categories WHERE slug = 'bulk-' || (1 + i % 20)), true, now(), now()
FROM generate_series(1, 10000) i;

INSERT INTO stock_items (product_id, on_hand, reserved, updated_at)
SELECT id, (abs(hashtext(sku)) % 50), 0, now() FROM products WHERE sku LIKE 'BULK-%';

INSERT INTO inventory_movements (product_id, type, quantity, reference, reason, user_id, created_at)
SELECT product_id, 'Receipt', on_hand, 'BULK-SEED', NULL, NULL, now() FROM stock_items s
JOIN products p ON p.id = s.product_id WHERE p.sku LIKE 'BULK-%' AND s.on_hand > 0;
COMMIT;
ANALYZE;
SELECT count(*) AS products FROM products;
