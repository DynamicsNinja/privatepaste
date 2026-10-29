# Dokploy template

`privatepaste/` is the template for the [Dokploy templates](https://github.com/Dokploy/templates) catalog, laid out as that repository expects in `blueprints/privatepaste/`:

| File | What |
|---|---|
| `docker-compose.yml` | The server from the published image `ghcr.io/dynamicsninja/privatepaste`, data in the `privatepaste-data` volume, port 8080 exposed to Dokploy's proxy only. |
| `template.toml` | The domain (service `privatepaste`, port 8080) and the environment. Dokploy generates `PP_DB_KEY` (32 random bytes, base64) and `PP_PASSWORD` (24 characters) for each install. |
| `meta.json` | Catalog entry: name, version, description, links, tags. |
| `privatepaste.svg` | Logo (`branding/logo.svg`). |

The `server` workflow starts this compose file with the freshly built image on every run, so a template that no longer works fails CI here first.

After deploying from the template, the password is in the service's environment in Dokploy. Back up `PP_DB_KEY` from there too: without it the data can't be recovered.

## Submitting or updating

Requirements first: the repository is public, it has a license, and the image version named in `docker-compose.yml` and `meta.json` is published (`server-vX.Y.Z` tag, see the `server` workflow) and public on GHCR (package settings → Change visibility).

1. Fork https://github.com/Dokploy/templates and create a branch, e.g. `add-privatepaste`.
2. Copy `privatepaste/` to `blueprints/privatepaste/` in the fork. Don't touch the root `meta.json`, it is generated.
3. In the fork, run `node build-scripts/generate-meta.js --check` (after `cd build-scripts && pnpm install`).
4. Open a pull request. Its preview deployment gives a Base64 template: in a Dokploy instance, create a Compose service → Advanced → Import, paste it, deploy, and check that the domain opens the sign-in page and an app can connect. The templates repository closes untested PRs, so note what you tested in the PR.

For a new server release: publish the image, bump the tag in `docker-compose.yml` and `version` in `meta.json` here, then send the same change to `blueprints/privatepaste/` in the templates repository.
