# Rate limits

## Limits by plan

API requests are limited to 60 requests per minute on Starter, 600 on Pro, and 6000 on Enterprise. Limits apply per workspace, not per key.

## Exceeding a limit

Requests over the limit return HTTP 429. The response includes a Retry-After header with the number of seconds to wait. Back off and retry after that interval.

