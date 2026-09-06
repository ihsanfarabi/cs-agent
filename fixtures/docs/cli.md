# CLI

## Installing

Install the CLI with npm (`npm install -g @nimbus/cli`) or with Homebrew (`brew install nimbus`).

## Logging in

Run `nimbus login` to authenticate. The command opens a browser and completes the OAuth flow.

## CI environments

For CI pipelines, create a personal access token and expose it as the NIMBUS_TOKEN environment variable. The CLI reads NIMBUS_TOKEN automatically and skips the browser flow.

