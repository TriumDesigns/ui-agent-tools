# Contributing

Thanks for helping! Bug reports, docs fixes and PRs are welcome.

## Before you open an issue
Run `./ui-agent.sh selftest` and include its output, plus your Unity version, render pipeline, OS and the
AI agent you use. Don't paste secrets or private project content.

## Pull requests
1. Branch from `develop` and open the PR against `develop` (`master` only receives releases).
2. Keep changes small and focused. Match the surrounding code style (C# 7-compatible, no new dependencies).
3. New files under the package need a `.meta` file (copy the importer style of a neighbour and use a new GUID).
4. Run `bash .github/scripts/check-package.sh`, and `./ui-agent.sh selftest` in a Unity project if you touched `Editor/`.
5. Update `CHANGELOG.md`.

By contributing you agree that your contribution is licensed under the Apache License 2.0 (see `LICENSE.md`),
as described in section 5 of that license.

## Releases
Maintainers bump `package.json`, the version in `README.md`/`SETUP.md` and `CHANGELOG.md` together, promote
`develop` to `master`, then tag `vX.Y.Z`.
