# Webhooks

## Creating an endpoint

Create a webhook endpoint from Settings → Webhooks by providing an HTTPS URL and selecting events. Webhooks deliver only over HTTPS.

## Failed deliveries

When a delivery fails, Nimbus retries it 5 times over 24 hours with exponential backoff. After the fifth failure the endpoint is disabled and must be re-enabled manually.

## Authenticating deliveries

Every delivery is signed with HMAC SHA-256. Verify the X-Nimbus-Signature header against your endpoint secret before trusting the payload.

