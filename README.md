Yahoo Finance Latest News Ingestor

This project loads all news from https://finance.yahoo.com/topic/latest-news/
and saves them to PostgreSQL.

Default mode uses Playwright.
It opens the page, scrolls it, collects news, and then uses the Yahoo API path to finish the feed faster.

There is also a direct API mode.

What is saved:
external_id
title
url
normalized_url
provider
published_at
summary
article_body
fetched_via
created_at
updated_at

Duplicates are blocked by external_id and normalized_url.
Running the service again inserts only new rows.

Main commands

Build:
docker compose build ingestor

Start PostgreSQL:
docker compose up -d postgres

Check database connection:
docker compose run --rm ingestor --check-db --log-level info

Run default mode:
docker compose run --rm ingestor --log-level info

Run API mode:
docker compose run --rm ingestor --source api --log-level info

Run with article body loading:
docker compose run --rm ingestor --fetch-body --log-level info

Run full scan without early stop on known pages:
docker compose run --rm ingestor --ignore-known-page --log-level info

Run Playwright without internal API backfill:
docker compose run --rm ingestor --playwright-browser-only --log-level info

Run with a custom connection string:
docker compose run --rm ingestor --connection-string "Host=host.docker.internal;Port=5432;Database=yahoo_news;Username=postgres;Password=postgres" --log-level info

Useful options
--source api|playwright
--fetch-body
--ignore-known-page
--playwright-browser-only
--max-items N
--api-page-size N
--body-concurrency N
--connection-string "..."
--check-db
--dry-run
--headless true|false
--timeout N
--log-level trace|debug|info|warn|error

Option notes
--fetch-body tries to load article_body for the items from the current run.
Running once without bodies and then running again with --fetch-body is supported.

--ignore-known-page disables the early stop on pages that are already fully known in the database.
It scans the full feed, but existing rows are still not duplicated or rewritten.

--playwright-browser-only disables the internal API backfill in Playwright mode.
Use it if you want a strict browser-only run.

--timeout sets browser and network timeout in seconds.

Run tests:
docker compose run --rm tests

Project layout
YahooFinanceIngestor/ main app
YahooFinanceIngestor.Tests/ tests
docker-compose.yml local PostgreSQL and run commands
