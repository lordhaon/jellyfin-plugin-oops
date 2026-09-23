# OOPS – Out Of Place Sorter

**Oops, wrong library?** OOPS is a Jellyfin plugin that lets an administrator move media from one library to another straight from the web UI. It moves the files on disk, rescans both libraries, and keeps everyone's watch history.

- **⋮ → Transfer media** on any movie, series, season, episode, album, artist folder, book or video
- **Multi-move:** select several items (long-press or the checkbox on hover), then open the selection menu and choose **Transfer media (N)**
- **Admins only.** The option is hidden from everyone else, and the API returns 403 to non-admins even if they call it directly.
- Works in the web browser and the official Jellyfin apps that use the web UI (Android, iOS, Jellyfin Media Player). Native third-party apps like Swiftfin, Findroid and Infuse won't show the option.

## Requirements

- Jellyfin **12.x**
- The [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin, which OOPS uses to add its menu option to the web UI
- Jellyfin must have **write access** to every library you move between. In Docker, mount those folders **read-write**, and mount them in the same container.

## Install

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add both:
   - File Transformation: `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
   - OOPS: `https://raw.githubusercontent.com/lordhaon/jellyfin-plugin-oops/main/manifest.json`
2. From **Catalog**, install **File Transformation** and **OOPS - Out Of Place Sorter**.
3. Restart Jellyfin, then hard-refresh the browser (Ctrl+F5) so the updated page loads.

## How moves work

| You pick | What moves on disk |
|---|---|
| Movie in its own folder | The whole folder (versions, extras, subtitles, artwork, NFOs) |
| Movie loose in the library root | The file plus its sidecars (`Movie.en.srt`, `Movie.nfo`, `Movie-poster.jpg`, `Movie.trickplay/`) |
| Series / season / album / artist | The whole folder |
| Single episode or track | The file plus its sidecars, placed at the same relative path (e.g. `Show/Season 1/`) in the target library |

- Only libraries of the **same type** are offered as targets (Movies → Movies, Shows → Shows, and so on).
- **Nothing is overwritten.** If a file already exists at the destination, that item is skipped, and nothing of it is moved.
- If a move fails partway through, the files that were already moved are put back.
- Moves across different drives work, but they copy the data and can take a while. Progress shows in the dialog.
- If the target library has several folders, OOPS uses the one that already contains the same series or artist folder. Otherwise it uses the first folder.
- After moving, OOPS scans the source and target libraries. It then copies each user's watched status, resume position, play count, favorite and rating onto the new items.

### Settings (Dashboard → Plugins → OOPS)

- **Keep watch history** (on by default)
- **Remove empty folders left behind** (on by default). Only deletes folders that are completely empty, never a library root.

The same page lists recent transfers and their results. The list is kept until Jellyfin restarts.

## Limitations

- Metadata edits stored only in Jellyfin's database (not in NFO files) are refetched in the new library. Turn on **NFO saving** in library settings if you hand-edit metadata.
- If Sonarr/Radarr/Lidarr manage these folders, update the root folder there too, or they'll see the files as missing.
- Moving the last season of a show leaves the show's folder behind if it still contains artwork or `tvshow.nfo`.
- The menu option depends on Jellyfin's web UI markup. A major Jellyfin web update may need a small script fix.

## Publishing (for the repo owner)

1. Create a GitHub repo named `jellyfin-plugin-oops` and push this folder to the `main` branch.
2. In `manifest.json`, replace `your-github-username` with your GitHub username. The release workflow also fills it in automatically.
3. Tag a release:
   ```bash
   git tag -a v1.0.0 -m "First release"
   git push origin v1.0.0
   ```
4. The **Release** action builds the DLL, attaches `oops_1.0.0.0.zip` to a GitHub release, and adds the version to `manifest.json` on `main`. Jellyfin picks up new versions from there.

To build locally: `dotnet build src/Jellyfin.Plugin.Oops/Jellyfin.Plugin.Oops.csproj -c Release`. This needs the .NET 10 SDK. To install without a repository, copy `Jellyfin.Plugin.Oops.dll` into `<jellyfin data>/plugins/OOPS/` and restart Jellyfin.

### When Jellyfin moves to a new major version

Update `JellyfinVersion` and `TargetFramework` in the `.csproj`, and `TARGET_ABI` in `scripts/update_manifest.py`, then tag a new release.

## API (admin only)

| Method | Path | Purpose |
|---|---|---|
| GET | `/OOPS/Targets?ids=<id,id>` | Which of the items can move, and to which libraries |
| POST | `/OOPS/Transfer` `{ "itemIds": [...], "targetLibraryId": "..." }` | Start a transfer; returns `{ "jobId": "..." }` |
| GET | `/OOPS/Jobs/{jobId}` | Progress and per-item results |
| GET | `/OOPS/Jobs` | Recent transfers |

## License

GPL-3.0
