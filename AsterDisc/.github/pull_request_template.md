## What this changes and why

<!-- A sentence or two. Link the issue it closes, if any. -->

## How it was tested

- [ ] `dotnet test Jellyfin.Plugin.DiscMenus.Tests`
- [ ] `python3 tools/validate.py`
- [ ] `cd tests/js && npm test`
- [ ] Tried it in a browser (for anything that changes how a menu looks or behaves)
- [ ] Added or updated tests (a bug fix includes a test that fails without it)

## Checklist

- [ ] No studio artwork, music or video added
- [ ] If a menu rule changed: the schema, `tools/validate.py` and `MenuFileLoader` all agree
- [ ] Docs updated if behaviour changed
