// WhisperSubs -- context menu integration
// Adds "Generate Subtitles" to the three-dot menu on item detail pages and cards (admin only).
// Loaded via script injection into Jellyfin's index.html.
(function () {
    'use strict';

    var isAdmin = null;
    var pendingItemId = null;
    var menuObserver = null;

    function checkAdmin() {
        if (isAdmin !== null) return Promise.resolve(isAdmin);
        return ApiClient.getCurrentUser().then(function (user) {
            isAdmin = user && user.Policy && user.Policy.IsAdministrator;
            return isAdmin;
        }).catch(function () { return false; });
    }

    function showToast(message) {
        try { require(['toast'], function (toast) { toast(message); }); }
        catch (e) { console.log('[WhisperSubs] ' + message); }
    }

    function closeDialog(el) {
        var dialog = el.closest('dialog');
        if (dialog && dialog.close) {
            dialog.close();
            return;
        }
        var btn = el.closest('.actionSheet');
        if (btn) {
            var cancel = btn.querySelector('.btnCloseActionSheet');
            if (cancel) cancel.click();
        }
    }

    function generateSubtitles(itemId) {
        var url = ApiClient.getUrl('Plugins/WhisperSubs/Items/' + itemId + '/GenerateAll', { language: 'auto' });
        return ApiClient.ajax({ type: 'POST', url: url });
    }

    function createMenuItem(itemId) {
        // Match Jellyfin's exact action sheet button structure
        var btn = document.createElement('button');
        btn.setAttribute('is', 'emby-button');
        btn.type = 'button';
        btn.className = 'listItem listItem-button actionSheetMenuItem btnWhisperSubs';
        btn.setAttribute('data-id', 'whispersubs');

        btn.innerHTML =
            '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons subtitles" aria-hidden="true"></span>' +
            '<div class="listItemBody actionsheetListItemBody">' +
                '<div class="listItemBodyText actionSheetItemText">Generate Subtitles</div>' +
            '</div>';

        btn.addEventListener('click', function () {
            closeDialog(btn);
            showToast('WhisperSubs: Queuing...');
            generateSubtitles(itemId).then(function (response) {
                var data = typeof response === 'string' ? JSON.parse(response) : response;
                var count = data.count || 1;
                showToast('WhisperSubs: Queued ' + count + ' item(s) for subtitle generation');
            }).catch(function () {
                showToast('WhisperSubs: Failed to queue generation');
            });
        });

        return btn;
    }

    function injectIntoActionSheet(sheet) {
        if (!pendingItemId) return;
        if (sheet.querySelector('.btnWhisperSubs')) return;

        checkAdmin().then(function (admin) {
            if (!admin) return;

            var scroller = sheet.querySelector('.actionSheetScroller') || sheet;
            var cancelDiv = scroller.querySelector('.buttons');
            var menuItem = createMenuItem(pendingItemId);

            if (cancelDiv) {
                scroller.insertBefore(menuItem, cancelDiv);
            } else {
                scroller.appendChild(menuItem);
            }
        });
    }

    function watchForActionSheet() {
        // Disconnect any previous observer
        if (menuObserver) menuObserver.disconnect();

        menuObserver = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                var added = mutations[i].addedNodes;
                for (var j = 0; j < added.length; j++) {
                    var node = added[j];
                    if (node.nodeType !== 1) continue;

                    var sheet = null;
                    if (node.classList && node.classList.contains('actionSheet')) {
                        sheet = node;
                    } else if (node.querySelector) {
                        sheet = node.querySelector('.actionSheet');
                    }

                    if (sheet) {
                        menuObserver.disconnect();
                        menuObserver = null;
                        injectIntoActionSheet(sheet);
                        return;
                    }
                }
            }
        });

        menuObserver.observe(document.body, { childList: true, subtree: true });

        // Auto-disconnect after 3 seconds
        setTimeout(function () {
            if (menuObserver) {
                menuObserver.disconnect();
                menuObserver = null;
            }
        }, 3000);
    }

    // Capture clicks on three-dot menu triggers everywhere
    document.addEventListener('click', function (e) {
        var trigger = e.target.closest('.btnMoreCommands, [data-action="menu"]');
        if (!trigger) return;

        // Try to get item ID from the nearest card/item element
        var card = trigger.closest('[data-id]');
        if (card) {
            pendingItemId = card.getAttribute('data-id');
        } else {
            // Detail page fallback: extract from URL hash
            var hash = window.location.hash || '';
            var q = hash.indexOf('?');
            if (q !== -1) {
                var params = new URLSearchParams(hash.substring(q + 1));
                pendingItemId = params.get('id');
            }
        }

        if (pendingItemId) {
            watchForActionSheet();
        }
    }, true); // capture phase to run before Jellyfin's handler

    console.debug('[WhisperSubs] Context menu integration loaded');
})();
