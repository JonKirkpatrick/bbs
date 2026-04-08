# BBS Documentation Site

This folder contains the Docusaurus site for project documentation.

## Prerequisites

- Node.js 20+ and npm

If your distro package manager installed Node 18, use a version manager (recommended):

```bash
# install nvm if needed
curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.3/install.sh | bash

# restart shell, then:
nvm install 20
nvm use 20
```

## Quick Start

From the repository root:

```bash
cd docs-site
npm install
npm run start
```

Then open `http://localhost:3000`.

## Build

```bash
cd docs-site
npm run build
npm run serve
```

## Content Source

The docs plugin is configured to read markdown files directly from `../docs`.
This keeps documentation content in one place while letting Docusaurus handle navigation, search, and versioning.

## Next Recommended Steps

- Add docs versioning when preparing the next release: `npm run docusaurus docs:version <version>`
- Add search (Algolia or local search plugin)
- Add CI for broken links (`npm run build`)

## Algolia DocSearch (Rough-In Complete)

Algolia support is wired in config and activates automatically when all required env vars are present.

Required variables:

- `ALGOLIA_APP_ID`
- `ALGOLIA_SEARCH_API_KEY` (search-only key)
- `ALGOLIA_INDEX_NAME`

Quick setup:

```bash
cd docs-site
cp .env.example .env.local
# edit .env.local and set the three values above
npm run start
```

Notes:

- If any variable is missing, Algolia stays disabled and the site still runs normally.
- The index must be populated by DocSearch crawler or your own Algolia crawler before results appear.

## Dependency Stability Notes

- This project pins Docusaurus packages to `3.10.0` and overrides `webpack` to `5.105.0`.
- Reason: newer webpack releases can trigger a ProgressPlugin schema incompatibility through `webpackbar` used by Docusaurus.
- If you change Docusaurus versions, retest `npm run start` and `npm run build` before committing lockfile changes.
