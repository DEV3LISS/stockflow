// Flash sale: BUYERS customers press "Place order" at the same moment for a product with STOCK units.
// Expected: exactly STOCK orders (201), all others 409 "Out of stock", zero 5xx.
//   docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:8081 \
//     grafana/k6:1.0.0 run - < loadtests/flash-sale.js
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import exec from 'k6/execution';

const BASE = __ENV.BASE_URL || 'http://localhost:8081';
const BUYERS = Number(__ENV.BUYERS || 300);
const STOCK = Number(__ENV.STOCK || 100);
const created = new Counter('orders_created');
const soldOut = new Counter('orders_out_of_stock');
const errors = new Counter('orders_errors');
const json = { headers: { 'Content-Type': 'application/json' } };

export const options = {
  setupTimeout: '180s',
  scenarios: { sale: { executor: 'per-vu-iterations', vus: BUYERS, iterations: 1, maxDuration: '60s' } },
  thresholds: { 'http_req_duration{name:checkout}': ['p(95)<2000'], orders_errors: ['count==0'] },
};

export function setup() {
  const admin = http.post(`${BASE}/api/v1/auth/login`, JSON.stringify({ email: 'admin@stockflow.local', password: 'ChangeMe-2026!' }), json).json('accessToken');
  const auth = { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${admin}` } };
  const run = Date.now();
  const cat = http.post(`${BASE}/api/v1/categories`, JSON.stringify({ name: `Sale ${run}`, slug: `sale-${run}` }), auth).json('id');
  const product = http.post(`${BASE}/api/v1/products`, JSON.stringify({ sku: `SALE-${run}`, name: 'Flash sale item', price: 999, categoryId: cat }), auth).json('id');
  http.post(`${BASE}/api/v1/inventory/${product}/receipts`, JSON.stringify({ quantity: STOCK, reference: 'SALE' }), auth);

  const tokens = [];
  for (let i = 0; i < BUYERS; i++) {
    const t = http.post(`${BASE}/api/v1/auth/register`,
      JSON.stringify({ email: `buyer${run}-${i}@sale.local`, fullName: `Buyer ${i}`, password: 'Buyer-Password-123' }), json).json('accessToken');
    http.put(`${BASE}/api/v1/cart/items`, JSON.stringify({ productId: product, quantity: 1 }),
      { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${t}` } });
    tokens.push(t);
  }
  return { tokens, product, admin };
}

export default function (data) {
  const token = data.tokens[exec.vu.idInTest - 1];
  const res = http.post(`${BASE}/api/v1/orders`, null, {
    headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': `${token.slice(-20)}-sale` },
    tags: { name: 'checkout' },
  });
  if (res.status === 201) created.add(1);
  else if (res.status === 409) soldOut.add(1);
  else errors.add(1);
  check(res, { 'no server error': (r) => r.status < 500 });
}

export function teardown(data) {
  const s = http.get(`${BASE}/api/v1/inventory/${data.product}`, { headers: { Authorization: `Bearer ${data.admin}` } }).json();
  console.log(`STOCK onHand=${s.onHand} reserved=${s.reserved} available=${s.available}`);
}
