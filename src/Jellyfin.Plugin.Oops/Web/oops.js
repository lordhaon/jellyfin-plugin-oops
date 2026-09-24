/* OOPS – Out Of Place Sorter: adds "Transfer media" to Jellyfin's item menus (admins only). */
(function () {
    'use strict';

    if (window.__oopsLoaded) {
        return;
    }
    window.__oopsLoaded = true;

    var MENU_ID = 'oops-transfer';
    var PENDING_MS = 3000;
    var pending = null;
    var adminCache = { userId: null, value: false, promise: null };

    // ---------- helpers ----------

    function client() {
        return window.ApiClient;
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function isAdmin() {
        var c = client();
        if (!c || typeof c.getCurrentUserId !== 'function' || !c.getCurrentUserId()) {
            return Promise.resolve(false);
        }
        var uid = c.getCurrentUserId();
        if (adminCache.userId === uid && adminCache.promise) {
            return adminCache.promise;
        }
        adminCache.userId = uid;
        adminCache.promise = c.getCurrentUser().then(function (u) {
            return !!(u && u.Policy && u.Policy.IsAdministrator);
        }).catch(function () {
            adminCache.promise = null;
            return false;
        });
        return adminCache.promise;
    }

    function getJson(path, params) {
        var c = client();
        return c.getJSON(c.getUrl(path, params));
    }

    function postJson(path, body) {
        var c = client();
        return c.ajax({
            type: 'POST',
            url: c.getUrl(path),
            data: JSON.stringify(body),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function idFromLocation() {
        var m = /[?&]id=([0-9a-fA-F-]{32,36})/.exec((location.hash || '') + '&' + (location.search || ''));
        return m ? m[1] : null;
    }

    function selectedIds() {
        var ids = [];
        document.querySelectorAll('.chkItemSelect:checked').forEach(function (chk) {
            var holder = chk.closest('[data-id]');
            var id = holder && holder.getAttribute('data-id');
            if (id && ids.indexOf(id) === -1) {
                ids.push(id);
            }
        });
        return ids;
    }

    function remember(ids) {
        pending = ids && ids.length ? { ids: ids, at: Date.now() } : null;
    }

    // ---------- work out which item(s) a menu is for ----------

    document.addEventListener('click', function (e) {
        var t = e.target;
        if (!(t instanceof Element)) {
            return;
        }

        if (t.closest('.btnSelectionPanelOptions')) {
            remember(selectedIds());
            return;
        }

        var menuBtn = t.closest('[data-action="menu"], .btnCardOptions');
        if (menuBtn) {
            var holder = menuBtn.closest('[data-id]');
            if (holder) {
                remember([holder.getAttribute('data-id')]);
            }
            return;
        }

        if (t.closest('.btnMoreCommands')) {
            var id = idFromLocation();
            if (id) {
                remember([id]);
            }
        }
    }, true);

    // Right-click / long-press on a card opens the same menu.
    document.addEventListener('contextmenu', function (e) {
        var t = e.target;
        if (!(t instanceof Element)) {
            return;
        }
        var holder = t.closest('.card[data-id], .listItem[data-id]');
        if (holder) {
            remember([holder.getAttribute('data-id')]);
        }
    }, true);

    // ---------- inject into the action sheet ----------

    function onSheetAdded(sheet) {
        if (!pending || Date.now() - pending.at > PENDING_MS) {
            return;
        }
        var ids = pending.ids.slice();
        pending = null;

        isAdmin().then(function (admin) {
            if (!admin) {
                return null;
            }
            return getJson('OOPS/Targets', { ids: ids.join(',') });
        }).then(function (targets) {
            if (!targets || !targets.libraries || !targets.libraries.length) {
                return;
            }
            var movable = targets.items.filter(function (i) { return i.canMove; });
            if (!movable.length || !document.body.contains(sheet)) {
                return;
            }
            addMenuButton(sheet, ids, targets, movable.length);
        }).catch(function () { /* not admin, or plugin unavailable */ });
    }

    function addMenuButton(sheet, ids, targets, count) {
        var scroller = sheet.querySelector('.actionSheetScroller');
        if (!scroller || scroller.querySelector('[data-id="' + MENU_ID + '"]')) {
            return;
        }

        var template = scroller.querySelector('.actionSheetMenuItem');
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.setAttribute('is', 'emby-button');
        // Keep Jellyfin's own classes so it looks native and closes the sheet when clicked.
        btn.className = template ? template.className : 'listItem listItem-button actionSheetMenuItem';
        btn.classList.add('emby-button');
        btn.setAttribute('data-id', MENU_ID);

        var hasIcons = !!scroller.querySelector('.actionsheetMenuItemIcon');
        var label = count > 1 ? 'Transfer media (' + count + ')' : 'Transfer media';
        btn.innerHTML = (hasIcons ? '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons drive_file_move" aria-hidden="true"></span>' : '')
            + '<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText">' + esc(label) + '</div></div>';

        btn.addEventListener('click', function () {
            setTimeout(function () { openDialog(ids, targets); }, 150);
        });

        scroller.appendChild(btn);
    }

    var observer = new MutationObserver(function (mutations) {
        mutations.forEach(function (m) {
            m.addedNodes.forEach(function (node) {
                if (!(node instanceof Element)) {
                    return;
                }
                var sheet = node.classList.contains('actionSheet') ? node : node.querySelector('.actionSheet');
                if (sheet) {
                    onSheetAdded(sheet);
                }
            });
        });
    });

    function startObserver() {
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.body) {
        startObserver();
    } else {
        document.addEventListener('DOMContentLoaded', startObserver);
    }

    // ---------- dialog ----------

    var css = ''
        + '.oops-overlay{position:fixed;inset:0;z-index:10000;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;padding:16px;}'
        + '.oops-dialog{background:#202020;color:#fff;border-radius:10px;width:100%;max-width:480px;max-height:90vh;overflow:auto;padding:20px 22px;box-shadow:0 10px 40px rgba(0,0,0,.5);font-size:15px;}'
        + '.oops-dialog h2{margin:0 0 4px;font-size:1.3em;}'
        + '.oops-sub{opacity:.7;margin-bottom:14px;font-size:.9em;}'
        + '.oops-lib{display:flex;align-items:center;gap:10px;padding:10px 12px;border-radius:6px;background:rgba(255,255,255,.06);margin-bottom:8px;cursor:pointer;}'
        + '.oops-lib:hover{background:rgba(255,255,255,.12);}'
        + '.oops-lib input{accent-color:#00a4dc;width:18px;height:18px;margin:0;}'
        + '.oops-lib small{display:block;opacity:.6;font-size:.8em;word-break:break-all;}'
        + '.oops-folder{display:flex;align-items:center;gap:10px;margin:6px 0 4px;}'
        + '.oops-folder[hidden]{display:none;}'
        + '.oops-folder select{flex:1;min-width:0;background:#2b2b2b;color:#fff;border:1px solid rgba(255,255,255,.2);border-radius:6px;padding:7px 8px;font-size:.9em;}'
        + '.oops-note{font-size:.85em;background:rgba(255,170,0,.12);border-left:3px solid #ffaa00;padding:8px 10px;margin:10px 0;border-radius:4px;}'
        + '.oops-actions{display:flex;justify-content:flex-end;gap:10px;margin-top:16px;}'
        + '.oops-btn{border:0;border-radius:6px;padding:9px 16px;font-size:.95em;cursor:pointer;background:rgba(255,255,255,.1);color:#fff;}'
        + '.oops-btn.primary{background:#00a4dc;}'
        + '.oops-btn:disabled{opacity:.4;cursor:default;}'
        + '.oops-bar{height:6px;background:rgba(255,255,255,.1);border-radius:3px;overflow:hidden;margin:12px 0;}'
        + '.oops-bar>div{height:100%;background:#00a4dc;transition:width .3s;}'
        + '.oops-results{list-style:none;padding:0;margin:8px 0 0;font-size:.9em;}'
        + '.oops-results li{padding:5px 0;border-bottom:1px solid rgba(255,255,255,.07);}'
        + '.oops-ok{color:#52b54b;}.oops-skip{color:#aaa;}.oops-fail{color:#ff6b6b;}';

    function ensureCss() {
        if (document.getElementById('oops-style')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'oops-style';
        style.textContent = css;
        document.head.appendChild(style);
    }

    function openDialog(ids, targets) {
        ensureCss();

        var overlay = document.createElement('div');
        overlay.className = 'oops-overlay';
        var dlg = document.createElement('div');
        dlg.className = 'oops-dialog';
        overlay.appendChild(dlg);
        document.body.appendChild(overlay);

        var running = false;
        function close() {
            document.removeEventListener('keydown', onKey, true);
            overlay.remove();
        }
        function onKey(e) {
            if (e.key === 'Escape' && !running) {
                e.stopPropagation();
                close();
            }
        }
        document.addEventListener('keydown', onKey, true);
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay && !running) {
                close();
            }
        });

        var movable = targets.items.filter(function (i) { return i.canMove; });
        var blocked = targets.items.filter(function (i) { return !i.canMove; });
        var sources = movable.map(function (i) { return i.sourceLibrary; }).filter(function (v, i, a) { return v && a.indexOf(v) === i; });

        var title = movable.length === 1 ? esc(movable[0].name) : movable.length + ' items';
        var html = '<h2>Transfer media</h2>'
            + '<div class="oops-sub">' + title + (sources.length ? ' · from ' + esc(sources.join(', ')) : '') + '</div>'
            + '<div>Move to:</div><div style="margin-top:8px;">';

        targets.libraries.forEach(function (lib, idx) {
            var folders = lib.folders && lib.folders.length ? lib.folders : [lib.folder];
            var hint = folders.length > 1 ? folders.length + ' folders' : folders[0];
            html += '<label class="oops-lib"><input type="radio" name="oops-target" value="' + esc(lib.id) + '"' + (idx === 0 ? ' checked' : '') + '>'
                + '<span>' + esc(lib.name) + '<small>' + esc(hint) + '</small></span></label>';
        });
        html += '</div>';
        html += '<div class="oops-folder" data-el="folder-row" hidden><label for="oops-folder">Folder:</label>'
            + '<select id="oops-folder" data-el="folder"></select></div>';

        if (blocked.length) {
            html += '<div class="oops-note"><b>These will be skipped:</b><br>' + blocked.map(function (b) {
                return esc(b.name) + ' – ' + esc(b.reason);
            }).join('<br>') + '</div>';
        }

        html += '<div class="oops-actions"><button class="oops-btn" data-act="cancel">Cancel</button>'
            + '<button class="oops-btn primary" data-act="go">Transfer</button></div>';
        dlg.innerHTML = html;

        var folderRow = dlg.querySelector('[data-el="folder-row"]');
        var folderSelect = dlg.querySelector('[data-el="folder"]');
        function libById(id) {
            return targets.libraries.filter(function (l) { return l.id === id; })[0];
        }
        // Only libraries with several folders get a choice; "Automatic" keeps shows/artists with their existing folder.
        function updateFolders() {
            var checked = dlg.querySelector('input[name="oops-target"]:checked');
            var lib = checked && libById(checked.value);
            var folders = lib && lib.folders ? lib.folders : [];
            folderRow.hidden = folders.length < 2;
            folderSelect.innerHTML = '<option value="">Automatic</option>' + folders.map(function (f) {
                return '<option value="' + esc(f) + '">' + esc(f) + '</option>';
            }).join('');
        }
        dlg.querySelectorAll('input[name="oops-target"]').forEach(function (r) {
            r.addEventListener('change', updateFolders);
        });
        updateFolders();

        dlg.querySelector('[data-act="cancel"]').addEventListener('click', close);
        dlg.querySelector('[data-act="go"]').addEventListener('click', function () {
            var checked = dlg.querySelector('input[name="oops-target"]:checked');
            if (!checked) {
                return;
            }
            running = true;
            var libName = libById(checked.value).name;
            var folder = folderRow.hidden ? null : (folderSelect.value || null);
            var moveIds = movable.map(function (i) { return i.id; });
            showProgress(dlg, libName, moveIds.length);

            postJson('OOPS/Transfer', { itemIds: moveIds, targetLibraryId: checked.value, targetFolder: folder }).then(function (res) {
                poll(dlg, res.jobId, function () {
                    running = false;
                    finish(dlg, close, ids);
                });
            }).catch(function (err) {
                running = false;
                showError(dlg, close, err);
            });
        });
    }

    function showProgress(dlg, libName, total) {
        dlg.innerHTML = '<h2>Transferring to ' + esc(libName) + '</h2>'
            + '<div class="oops-sub" data-el="state">Starting…</div>'
            + '<div class="oops-bar"><div data-el="bar" style="width:0%"></div></div>'
            + '<div class="oops-sub">You can keep using Jellyfin. Large files across drives can take a while.</div>'
            + '<ul class="oops-results" data-el="results"></ul>'
            + '<div class="oops-actions" data-el="actions"></div>';
        dlg.setAttribute('data-total', String(total));
    }

    function renderJob(dlg, job) {
        var total = job.total || Number(dlg.getAttribute('data-total')) || 1;
        var pct = job.isFinished ? 100 : Math.min(95, Math.round((job.done / total) * 80) + (job.state === 'Scanning libraries' ? 10 : 0) + (job.state === 'Restoring watch state' ? 15 : 0));
        dlg.querySelector('[data-el="state"]').textContent = job.state + (job.error ? ' – ' + job.error : '');
        dlg.querySelector('[data-el="bar"]').style.width = pct + '%';
        dlg.querySelector('[data-el="results"]').innerHTML = (job.items || []).map(function (i) {
            var cls = i.status === 'Moved' ? 'oops-ok' : (i.status === 'Skipped' ? 'oops-skip' : 'oops-fail');
            return '<li><span class="' + cls + '">' + esc(i.status) + '</span> · ' + esc(i.name)
                + (i.message ? '<br><small style="opacity:.75">' + esc(i.message) + '</small>' : '') + '</li>';
        }).join('');
    }

    function poll(dlg, jobId, done) {
        getJson('OOPS/Jobs/' + jobId).then(function (job) {
            renderJob(dlg, job);
            if (job.isFinished) {
                done(job);
            } else {
                setTimeout(function () { poll(dlg, jobId, done); }, 1500);
            }
        }).catch(function () {
            setTimeout(function () { poll(dlg, jobId, done); }, 3000);
        });
    }

    function finish(dlg, close, ids) {
        var actions = dlg.querySelector('[data-el="actions"]');
        var onMovedDetails = ids.indexOf(idFromLocation()) !== -1;
        actions.innerHTML = '<button class="oops-btn" data-act="close">Close</button>'
            + '<button class="oops-btn primary" data-act="refresh">' + (onMovedDetails ? 'Go back' : 'Refresh page') + '</button>';
        actions.querySelector('[data-act="close"]').addEventListener('click', close);
        actions.querySelector('[data-act="refresh"]').addEventListener('click', function () {
            close();
            if (onMovedDetails) {
                history.back();
            } else {
                location.reload();
            }
        });
    }

    function showError(dlg, close, err) {
        var msg = 'The transfer could not be started.';
        if (err && err.status === 403) {
            msg = 'Only administrators can transfer media.';
        }
        dlg.innerHTML = '<h2>Transfer media</h2><div class="oops-note">' + esc(msg) + '</div>'
            + '<div class="oops-actions"><button class="oops-btn" data-act="close">Close</button></div>';
        dlg.querySelector('[data-act="close"]').addEventListener('click', close);
    }

    // Warm the admin check so the menu option appears instantly.
    setTimeout(function () { isAdmin(); }, 2000);
})();
