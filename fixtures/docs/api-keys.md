# API keys

## Rotating keys

Rotate a key from the dashboard: Settings → API keys → Rotate. Rotation generates a new key immediately. The old key keeps working for 24 hours after rotation, then expires automatically. Plan deployments so consumers pick up the new key within that window.

## Key limits

Each workspace can have two active keys at the same time. Creating a third key requires rotating an existing one first.

## Key permissions

Keys inherit the role of the member who created them. A key created by a read-only member cannot deploy.

