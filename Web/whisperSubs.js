// WhisperSubs -- library detail page integration
// Injects "Generate Subtitles" buttons on item detail pages (admin only).
// Loaded via script injection into Jellyfin's index.html.
(function () {
    'use strict';

    var PLUGIN_ID = '97124bd9-c8cd-4a53-a213-e593aa3fef52';
    var BUTTON_CLASS = 'btnWhisperSubs';

    // Item types that support batch generation (enqueue all children)
    var BATCH_TYPES = ['Series', 'Season', 'MusicAlbum', 'BoxSet', 'Playlist'];
    // Item types that support single-item generation
    var LEAF_TYPES = ['Movie', 'Episode', 'Audio'];

    var isAdmin = null; // cached after first check

    function checkAdmin() {
        if (isAdmin !== null) {
            return Promise.resolve(isAdmin);
        }
        return ApiClient.getCurrentUser().then(function (user) {
            isAdmin = user && user.Policy && user.Policy.IsAdministrator;
            return isAdmin;
        }).catch(function () {
            return false;
        });
    }

    function showToast(message) {
        if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
            Dashboard.alert(message);
        } else {
            // Fallback: use Jellyfin's require-based toast if available
            try {
                require(['toast'], function (toast) { toast(message); });
            } catch (e) {
                console.log('[WhisperSubs] ' + message);
            }
        }
    }

    function generateSingle(itemId) {
        var url = ApiClient.getUrl('Plugins/WhisperSubs/Items/' + itemId + '/Generate', { language: 'auto' });
        return ApiClient.ajax({ type: 'POST', url: url });
    }

    function generateAll(itemId) {
        var url = ApiClient.getUrl('Plugins/WhisperSubs/Items/' + itemId + '/GenerateAll', { language: 'auto' });
        return ApiClient.ajax({ type: 'POST', url: url });
    }

    function onButtonClick(itemId, itemType, itemName, button) {
        button.disabled = true;
        var icon = button.querySelector('.detailButton-icon');
        if (icon) icon.textContent = 'hourglass_empty';

        var isBatch = BATCH_TYPES.indexOf(itemType) !== -1;
        var promise = isBatch ? generateAll(itemId) : generateSingle(itemId);

        promise.then(function (response) {
            var data = typeof response === 'string' ? JSON.parse(response) : response;
            var msg = isBatch
                ? 'WhisperSubs: Queued ' + (data.count || 'all') + ' item(s) for "' + itemName + '"'
                : 'WhisperSubs: Queued "' + itemName + '" for subtitle generation';
            showToast(msg);
            if (icon) icon.textContent = 'check';
            setTimeout(function () {
                if (icon) icon.textContent = 'subtitles';
                button.disabled = false;
            }, 3000);
        }).catch(function (err) {
            console.error('[WhisperSubs] Generation failed:', err);
            showToast('WhisperSubs: Failed to queue generation');
            if (icon) icon.textContent = 'subtitles';
            button.disabled = false;
        });
    }

    function createButton(itemId, itemType, itemName) {
        var isBatch = BATCH_TYPES.indexOf(itemType) !== -1;
        var label = isBatch ? 'Generate All Subtitles' : 'Generate Subtitles';

        var button = document.createElement('button');
        button.setAttribute('is', 'emby-button');
        button.type = 'button';
        button.className = 'button-flat ' + BUTTON_CLASS + ' detailButton emby-button';
        button.title = label;

        // Match Jellyfin's detail button structure
        button.innerHTML =
            '<div class="detailButton-content">' +
                '<span class="material-icons detailButton-icon subtitles" aria-hidden="true"></span>' +
                '<span class="detailButton-text">' + label + '</span>' +
            '</div>';

        button.addEventListener('click', function () {
            onButtonClick(itemId, itemType, itemName, button);
        });

        return button;
    }

    function injectButton(itemId, itemType, itemName) {
        // Find the detail buttons container
        var container = document.querySelector('.mainDetailButtons');
        if (!container) return;

        // Don't inject twice
        if (container.querySelector('.' + BUTTON_CLASS)) return;

        var button = createButton(itemId, itemType, itemName);

        // Insert before the "more commands" (three-dot) button if present
        var moreBtn = container.querySelector('.btnMoreCommands');
        if (moreBtn) {
            container.insertBefore(button, moreBtn);
        } else {
            container.appendChild(button);
        }
    }

    function waitForElement(selector, timeout) {
        timeout = timeout || 5000;
        return new Promise(function (resolve) {
            var el = document.querySelector(selector);
            if (el) return resolve(el);

            var observer = new MutationObserver(function () {
                var found = document.querySelector(selector);
                if (found) {
                    observer.disconnect();
                    resolve(found);
                }
            });
            observer.observe(document.body, { childList: true, subtree: true });
            setTimeout(function () {
                observer.disconnect();
                resolve(null);
            }, timeout);
        });
    }

    function onViewShow() {
        var hash = window.location.hash || '';
        // Item detail pages: #!/details?id=xxx or #/details?id=xxx
        if (hash.indexOf('details') === -1) return;

        var qmark = hash.indexOf('?');
        if (qmark === -1) return;

        var params = new URLSearchParams(hash.substring(qmark + 1));
        var itemId = params.get('id');
        if (!itemId) return;

        checkAdmin().then(function (admin) {
            if (!admin) return;

            // Fetch item info to know the type
            ApiClient.getItem(ApiClient.getCurrentUserId(), itemId).then(function (item) {
                if (!item || !item.Type) return;

                var supported = BATCH_TYPES.indexOf(item.Type) !== -1 || LEAF_TYPES.indexOf(item.Type) !== -1;
                if (!supported) return;

                waitForElement('.mainDetailButtons').then(function (container) {
                    if (container) {
                        injectButton(itemId, item.Type, item.Name);
                    }
                });
            }).catch(function (err) {
                console.debug('[WhisperSubs] Could not fetch item:', err);
            });
        });
    }

    // Register the viewshow listener
    document.addEventListener('viewshow', onViewShow);

    console.debug('[WhisperSubs] Library integration loaded');
})();
