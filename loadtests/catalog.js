// Catalog read load at a fixed rate (open model): 200 requests/second for 30 s.
// 60 % category pages, 25 % product pages, 15 % search.
//   docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:8081 \
//     grafana/k6:1.0.0 run - < loadtests/catalog.js
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL || 'http://localhost:8081';
const RATE = Number(__ENV.RATE || 200);

export const options = {
  scenarios: {
    catalog: { executor: 'constant-arrival-rate', rate: RATE, timeUnit: '1s', duration: '30s', preAllocatedVUs: 50, maxVUs: 400 },
  },
  thresholds: { http_req_failed: ['rate<0.01'], http_req_duration: ['p(95)<50'] },
};

export function setup() {
  const ids = [];
  for (let page = 1; page <= 5; page++) {
    const res = http.get(`${BASE}/api/v1/products?category=bulk-${page}&pageSize=100`);
    res.json('items').forEach((p) => ids.push(p.id));
  }
  return { ids };
}

export default function (data) {
  const r = Math.random();
  let res;
  if (r < 0.6) {
    const cat = 1 + Math.floor(Math.random() * 20);
    const page = 1 + Math.floor(Math.random() * 5);
    res = http.get(`${BASE}/api/v1/products?category=bulk-${cat}&page=${page}&sort=PriceAsc`, { tags: { name: 'category' } });
  } else if (r < 0.85) {
    const id = data.ids[Math.floor(Math.random() * data.ids.length)];
    res = http.get(`${BASE}/api/v1/products/${id}`, { tags: { name: 'product' } });
  } else {
    const words = ['phone', 'laptop', 'model 12', 'watch', 'speaker'];
    res = http.get(`${BASE}/api/v1/products?search=${encodeURIComponent(words[Math.floor(Math.random() * words.length)])}`, { tags: { name: 'search' } });
  }
  check(res, { '200': (x) => x.status === 200 });
}
