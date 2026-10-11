# Public snapshot validation

Checked on 2026-10-11, using Unity 2022.3.55f1 and Node.js 22.23.2.

- `npm ci --ignore-scripts` and `npm test` in `Server/backend`: **33 tests passed**.
- Clean Unity project import and script compilation: passed.
- `ProjectSetup.ImportTmpEssentials` from batch mode: completed, with generated package assets kept local and ignored.
- `GameplayChecks.Run`: **ALL PASSED** (translation coverage, Chinese glyphs, independent font atlas, immediate language changes, preservation of player/room names, legal loadout selection, death-screen cursor and loadout).
- `bash -n` on deployment scripts: passed. AWS deployment commands were not executed for this publication.
- Local documentation links: checked.
- `tools/check_public.py`: excluded paths and common AWS/GitHub/Slack/private-key patterns checked in all tracked files. This is a focused check, not a guarantee against every possible secret format.
- Production account number, local absolute workspace paths and the production API identifier were checked and absent from public files.

This edition contains no production scene or avatar prefabs. A full playable client/server build and gameplay validation of a reconstructed asset project were **not** performed. Source compilation and asset-independent UI/backend tests do not establish full game playability. The new minimal input controller compiles but has not undergone the production game's multiplayer movement playtests.

## Input/default/spawn update

- Public-source `GameplayChecks.Run` passed again after the red-dot preference migration and Esc/P menu changes. Pause cursor ownership, input blocking, resume behavior and the displayed alternative shortcut are covered.
- WebGL graphics and browser keyboard hooks are included under `Assets/Plugins/WebGL/`.
- Production asset project checks confirmed eight ground-level warehouse spawns, excluding rooftop points 12 and 13; all three local multiplayer modes passed. Those production asset files remain excluded here.
- The compiled production WebGL client was checked in Chrome with Keyboard Lock permission granted: short Esc opened the menu without exiting Fullscreen API mode; P also preserved fullscreen. Unsupported or permission-denied browsers use P.
