// Stands in for the parts of Jellyfin's dashboard that configPage.html calls,
// so the page from the Jellyfin plugin runs unchanged inside Silo.
(function () {
    'use strict';

    // The page is served at <base>/admin and every Moonbase route lives under
    // <base>/Moonfin, where <base> is this installation's plugin path in Silo.
    var base = location.origin + location.pathname.replace(/\/admin\/?$/, '');
    var draftKey = 'moonbaseConfigDraft:' + base;

    function getUrl(name, params) {
        var url = base + (name.charAt(0) === '/' ? '' : '/') + name;
        var query = new URLSearchParams();
        Object.keys(params || {}).forEach(function (k) {
            if (params[k] !== undefined && params[k] !== null) query.append(k, params[k]);
        });
        var qs = query.toString();
        return qs ? url + (url.indexOf('?') < 0 ? '?' : '&') + qs : url;
    }

    // Silo signs these requests with the HttpOnly launch cookie it set when the
    // page was opened. It lasts five minutes, and Silo replaces it with a fresh
    // one on a request once it is half way through, so a ping each minute
    // keeps the page signed in for as long as the admin's Silo login lasts.
    var nativeFetch = window.fetch.bind(window);
    var keepAlive = setInterval(function () {
        window.fetch(getUrl('Moonfin/Ping'), { cache: 'no-store' }).catch(function () { /* the next one tries again */ });
    }, 60 * 1000);
    window.fetch = function (input, init) {
        return nativeFetch(input, init).then(function (response) {
            if (response.status === 401) showSessionEnded();
            return response;
        });
    };

    function send(method, url, body, contentType) {
        var init = { method: method, headers: {} };
        if (body !== undefined && body !== null) {
            init.body = body;
            if (contentType) init.headers['Content-Type'] = contentType;
        }
        return window.fetch(url, init).then(function (response) {
            if (!response.ok) throw response;
            return response;
        });
    }

    function json(response) {
        return response.status === 204 ? null : response.json();
    }

    window.ApiClient = {
        serverAddress: function () { return base; },
        getUrl: getUrl,
        accessToken: function () { return ''; },
        getCurrentUserId: function () { return 'silo'; },
        ajax: function (request) {
            var body = request.data;
            var contentType = request.contentType;
            if (body !== undefined && typeof body !== 'string') {
                body = JSON.stringify(body);
                contentType = contentType || 'application/json';
            }
            return send(request.type || 'GET', request.url, body, contentType).then(function (response) {
                return request.dataType === 'json' ? json(response) : response;
            });
        },
        getJSON: function (url) {
            return send('GET', url).then(json);
        },
        getPluginConfiguration: function () {
            // Saving reads the configuration again, so only the first read
            // after opening the page may take the draft. It stays stored
            // until a save goes through.
            var draft = draftOffered ? null : (layerSwitch && layerSwitch.config) || readDraft();
            if (draft && !draftOffered && !layerSwitch) {
                notice('Restored the changes you hadn’t saved before your Silo session for this page ended. Press Save to keep them.');
            }
            draftOffered = true;
            if (draft) {
                lastConfig = draft;
                return Promise.resolve(layerView(draft));
            }
            return this.getJSON(getUrl('Moonfin/Admin/Configuration')).then(function (config) {
                lastConfig = config;
                return layerView(config);
            }, function (error) {
                // Saving reads the configuration first. Once the launch cookie
                // has run out, carry on from the copy already loaded so the
                // save fails on its own request and keeps the changes.
                var status = error && error.status;
                if (status === 401 && lastConfig) return layerView(lastConfig);
                // The page has no failure handler of its own, so its loading
                // overlay would stay up over the settings.
                Dashboard.hideLoadingMsg();
                if (status !== 401) toast('Silo didn’t load the settings' + (status ? ' (status ' + status + ').' : '.'));
                throw error;
            });
        },
        updatePluginConfiguration: function (id, config) {
            config = layersOf(config);
            if (switchTo) {
                // A device picked in the defaults: the page fills its form
                // only when it loads, so it loads again with this copy.
                try {
                    sessionStorage.setItem(layerKey, JSON.stringify({ layer: switchTo, config: config }));
                    location.reload();
                    return new Promise(function () { /* the page reloads */ });
                } catch (e) {
                    switchTo = null;
                    Dashboard.hideLoadingMsg();
                    toast('Save first, then pick the device.');
                    return Promise.reject(e);
                }
            }
            return send('POST', getUrl('Moonfin/Admin/Configuration'), JSON.stringify(config), 'application/json').then(function () {
                clearDraft();
            }, function (error) {
                Dashboard.hideLoadingMsg();
                var status = error && error.status;
                if (status === 401) {
                    saveDraft(config);
                } else {
                    toast('Silo didn’t save the settings' + (status ? ' (status ' + status + ').' : '.'));
                }
                throw new Error('Saving the Moonbase settings failed' + (status ? ' with status ' + status : ''));
            });
        },
        // Silo doesn't tell plugins about its accounts, so this lists the
        // ones that have used Moonfin with this server.
        getUsers: function () {
            return this.getJSON(getUrl('Moonfin/Admin/Users')).then(function (users) {
                // After the page has filled its single-user list from them.
                setTimeout(function () { addProfileOptions(users); });
                return users;
            });
        },
        getUserViews: function () {
            return this.getJSON(getUrl('Moonfin/Silo/Whoami')).then(function (who) {
                var types = { movie: 'movies', tv: 'tvshows' };
                return {
                    Items: (who && who.libraries || []).map(function (l) {
                        return { Id: l.id, Name: l.name, CollectionType: types[l.mediaType] || l.mediaType };
                    })
                };
            });
        },
        // Silo gives plugins no way to read library items.
        getItems: function () {
            return Promise.resolve({ Items: [], TotalRecordCount: 0 });
        }
    };

    var lastConfig = null;
    var draftOffered = false;

    // Moonbase on Silo only: admin defaults per device type. The page edits
    // DefaultUserSettings, so it's handed one layer at a time: the defaults
    // for all devices, or one device's on top of them. A device keeps only
    // what differs from all devices, in DefaultDeviceSettings.
    var layerKey = 'moonbaseDefaultsLayer:' + base;
    var layerSwitch = null;
    try {
        layerSwitch = JSON.parse(sessionStorage.getItem(layerKey) || 'null');
        sessionStorage.removeItem(layerKey);
    } catch (e) { /* starts on all devices */ }
    var devices = ['desktop', 'mobile', 'tv'];
    var layer = layerSwitch ? layerSwitch.layer : 'global';
    var switchTo = null;
    var layers = null;

    function defaultsFor(name) {
        var all = layers.global;
        if (name === 'global') return all;
        var own = layers[name];
        var view = Object.assign({}, all);
        // A device's home layout replaces the shared one whole, as on the server.
        if (own.homeSections != null || own.homeRowOrder != null) {
            delete view.homeSections;
            delete view.homeRowOrder;
        }
        return Object.assign(view, own);
    }

    // The configuration as the page sees it: the layer picked in the
    // defaults, with an uploaded default theme as "custom:<id>".
    function layerView(config) {
        if (!layers) {
            var saved = config.DefaultDeviceSettings || {};
            layers = { global: config.DefaultUserSettings || {} };
            devices.forEach(function (d) { layers[d] = saved[d] || {}; });
        }
        var view = clone(config);
        var defaults = view.DefaultUserSettings = clone(defaultsFor(layer));
        if (defaults.customThemeId) defaults.visualTheme = 'custom:' + defaults.customThemeId;
        fillUploadedThemes(config.UploadedThemes);
        return view;
    }

    // Takes the page's DefaultUserSettings back into the layer it showed and
    // returns the configuration to store.
    function layersOf(config) {
        var shown = config.DefaultUserSettings || {};
        var theme = shown.visualTheme || '';
        var custom = theme.indexOf('custom:') === 0;
        shown.customThemeId = custom ? theme.slice(7) : null;
        if (custom) shown.visualTheme = null;
        if (layer === 'global') {
            layers.global = shown;
        } else {
            var own = {};
            Object.keys(shown).forEach(function (k) {
                if (shown[k] != null && canonicalJson(shown[k]) !== canonicalJson(layers.global[k])) own[k] = shown[k];
            });
            if (own.homeSections != null || own.homeRowOrder != null) {
                own.homeSections = shown.homeSections;
                own.homeRowOrder = shown.homeRowOrder;
            }
            layers[layer] = own;
        }
        config.DefaultUserSettings = layers.global;
        config.DefaultDeviceSettings = {};
        devices.forEach(function (d) { config.DefaultDeviceSettings[d] = layers[d]; });
        return config;
    }

    // Server JSON comes back with sorted keys, so compare that way.
    function canonicalJson(value) {
        if (Array.isArray(value)) return '[' + value.map(canonicalJson).join(',') + ']';
        if (value && typeof value === 'object') {
            return '{' + Object.keys(value).sort().map(function (k) {
                return JSON.stringify(k) + ':' + canonicalJson(value[k]);
            }).join(',') + '}';
        }
        return JSON.stringify(value == null ? null : value);
    }

    // Uploaded themes join the App Theme default as "custom:<id>". The list
    // is the one the configuration was loaded with.
    function fillUploadedThemes(items) {
        var select = document.getElementById('DefaultVisualTheme');
        if (!select) return;
        var group = document.getElementById('MoonbaseUploadedThemes');
        if (!group) {
            group = document.createElement('optgroup');
            group.id = 'MoonbaseUploadedThemes';
            group.label = 'Uploaded';
            select.appendChild(group);
        }
        // Saving reads the configuration again after the admin picked a theme.
        var current = select.value;
        group.innerHTML = '';
        (items || []).forEach(function (item) {
            var id = item.id || item.Id;
            if (!id) return;
            var option = document.createElement('option');
            option.value = 'custom:' + id;
            option.textContent = item.displayName || item.DisplayName || id;
            group.appendChild(option);
        });
        group.hidden = !group.children.length;
        select.value = current;
    }

    function addDefaultsLayerPicker(page) {
        var bar = page.querySelector('.moonfinTabPanel[data-tab="defaults"] .defaultsSubnavBar');
        var form = document.getElementById('MoonfinConfigForm');
        if (!bar || !form) return;
        var box = document.createElement('div');
        box.className = 'inputContainer';
        box.innerHTML = '<label class="inputLabel inputLabelUnfocused" for="MoonbaseDefaultsLayer">Defaults For</label>'
            + '<select id="MoonbaseDefaultsLayer" is="emby-select">'
            + '<option value="global">All devices</option><option value="desktop">Desktop</option>'
            + '<option value="mobile">Mobile</option><option value="tv">TV</option></select>'
            + '<div class="fieldDescription">A device starts out showing your all-devices values. Anything you change there applies to that device only; set it back to the all-devices value, or to Not set, to follow all devices again. Changes are kept when you switch, and Save stores every device. A user’s own settings still come first, then your defaults for their device, then your defaults for all devices.</div>';
        bar.before(box);
        upgrade(box);
        var select = box.querySelector('select');
        select.value = layer;
        select.addEventListener('change', function () {
            var next = select.value;
            select.value = layer;
            if (!form.reportValidity()) return;
            switchTo = next;
            form.requestSubmit();
        });
    }

    function clone(value) {
        return JSON.parse(JSON.stringify(value));
    }

    // A draft older than this is dropped, because someone may have changed
    // the settings in the meantime.
    var draftMaxAgeMs = 60 * 60 * 1000;

    function readDraft() {
        try {
            var draft = JSON.parse(sessionStorage.getItem(draftKey) || 'null');
            if (draft && Date.now() - draft.savedAt < draftMaxAgeMs) return draft.config;
            clearDraft();
        } catch (e) { /* no usable draft */ }
        return null;
    }
    function saveDraft(config) {
        try { sessionStorage.setItem(draftKey, JSON.stringify({ savedAt: Date.now(), config: config })); } catch (e) { /* the banner still explains */ }
    }
    function clearDraft() {
        try { sessionStorage.removeItem(draftKey); } catch (e) { /* nothing stored */ }
    }

    var loadingCount = 0;
    function loadingEl() {
        var el = document.getElementById('moonbaseLoading');
        if (!el) {
            el = document.createElement('div');
            el.id = 'moonbaseLoading';
            el.className = 'moonbaseLoading';
            el.innerHTML = '<div class="moonbaseSpinner" role="progressbar" aria-label="Loading"></div>';
            document.body.appendChild(el);
        }
        return el;
    }

    function toast(text) {
        var el = document.createElement('div');
        el.className = 'moonbaseToast';
        el.textContent = text;
        document.body.appendChild(el);
        setTimeout(function () { el.classList.add('is-visible'); }, 10);
        setTimeout(function () {
            el.classList.remove('is-visible');
            setTimeout(function () { el.remove(); }, 300);
        }, 3300);
    }

    window.Dashboard = {
        showLoadingMsg: function () {
            loadingCount++;
            loadingEl().classList.add('is-visible');
        },
        hideLoadingMsg: function () {
            loadingCount = Math.max(0, loadingCount - 1);
            if (!loadingCount) loadingEl().classList.remove('is-visible');
        },
        processPluginConfigurationUpdateResult: function () {
            loadingCount = 0;
            loadingEl().classList.remove('is-visible');
            toast('Settings saved.');
        }
    };

    function notice(text, warn) {
        var bar = document.querySelector('.moonbaseHostNotices');
        if (!bar) return;
        var el = document.createElement('div');
        el.className = 'moonbaseHostNotice' + (warn ? ' is-warning' : '');
        el.textContent = text;
        bar.appendChild(el);
    }

    var sessionEndedShown = false;
    function showSessionEnded() {
        if (sessionEndedShown) return;
        sessionEndedShown = true;
        clearInterval(keepAlive);
        notice('Your Silo session for this page has ended. Open Moonbase again from Plugin Apps in Silo’s admin menu. Changes you tried to save are kept and come back when you do.', true);
        window.scrollTo(0, 0);
    }

    // Jellyfin's dashboard upgrades these elements when a page loads. The page's
    // CSS expects the upgraded markup.
    function upgrade(root) {
        root.querySelectorAll('input[is="emby-checkbox"]:not([data-embycheckbox])').forEach(function (input) {
            input.setAttribute('data-embycheckbox', 'true');
            input.classList.add('emby-checkbox');
            var label = input.closest('label');
            if (!label) return;
            label.classList.add('emby-checkbox-label');
            var outline = document.createElement('span');
            outline.className = 'checkboxOutline';
            outline.innerHTML = '<span class="material-icons checkboxIcon checkboxIcon-checked" aria-hidden="true"></span>';
            label.appendChild(outline);
        });
        [['input', 'emby-input'], ['select', 'emby-select'], ['button', 'emby-button'], ['textarea', 'emby-textarea']].forEach(function (pair) {
            root.querySelectorAll(pair[0] + '[is="' + pair[1] + '"]').forEach(function (el) { el.classList.add(pair[1]); });
        });
    }

    // The copied page names the server Jellyfin. Only text a person reads is
    // changed, never code or values.
    function renameServer(root) {
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                var tag = node.parentNode && node.parentNode.nodeName;
                return tag === 'SCRIPT' || tag === 'STYLE' ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
            }
        });
        var node;
        while ((node = walker.nextNode())) {
            if (/jellyfin/i.test(node.nodeValue)) node.nodeValue = swap(node.nodeValue);
        }
        root.querySelectorAll('[placeholder*="ellyfin"], [title*="ellyfin"]').forEach(function (el) {
            ['placeholder', 'title'].forEach(function (attr) {
                if (el.hasAttribute(attr)) el.setAttribute(attr, swap(el.getAttribute(attr)));
            });
        });
    }
    function swap(text) {
        return text
            .replace(/\s*\(Jellyfin \d+\+\)/g, '')
            .replace(/jellyfin\.example\.com/g, 'silo.example.com')
            .replace(/:8096\b/g, ':8090')
            .replace(/\bjellyfin-web\b/g, 'Silo’s web app')
            .replace(/\bJellyfin\b/g, 'Silo');
    }

    // Sections for things Silo does itself or will never use. They're hidden,
    // not removed, so their settings still load and save unchanged.
    var hiddenSections = [
        'Connecting to this server',
        'Seerr Integration',
        'Recommendations',
        'Live TV Recordings',
        'Web Plugin Status',
        'Active Downloads'
    ];

    // Fields of the user defaults that only matter for those features.
    var hiddenFields = ['DefaultShowLiveTvButton', 'DefaultLiveTvChannelSortBy', 'DefaultEpgMobileView', 'DefaultSeerrOffNote'];

    // Notes for the sections Silo can't run yet, because of what Silo lets
    // plugins do today. Their settings still save, so nothing is lost.
    var unsupported = {
        'IMDb Charts': 'Not supported on Silo yet: Silo doesn’t let plugins read its library, which the charts are matched against. These settings do nothing on Silo for now.',
        'Anime Filler & Recap Markers': 'Not supported on Silo yet: Silo doesn’t let plugins read its library. These settings do nothing on Silo for now.',
        'Retro Games (EmulatorJS)': 'Not supported on Silo yet: Silo doesn’t let plugins read game files. These settings do nothing on Silo for now.',
        'MDBList Integration': 'Ratings work on Silo. The official MDBList lists aren’t supported on Silo yet, because Silo doesn’t let plugins read its library.',
        'PINs': 'Not supported on Silo yet: Moonbase on Silo doesn’t keep PINs. Apps use the PIN stored on the device.',
        'Uploaded Themes': 'Moonbase on Silo keeps up to 5 themes. Apps pick up a new or changed theme the next time they sign in.'
    };

    function adaptSections(root) {
        root.querySelectorAll('.sectionTitle').forEach(function (title) {
            var name = title.textContent.trim();
            if (hiddenSections.indexOf(name) >= 0) {
                title.closest('.verticalSection').classList.add('moonbaseHidden');
                return;
            }
            var text = unsupported[name];
            if (!text) return;
            var note = document.createElement('div');
            note.className = 'moonbaseSiloNote';
            note.textContent = text;
            title.insertAdjacentElement('afterend', note);
        });
        hiddenFields.forEach(function (id) {
            var el = document.getElementById(id);
            if (el) (el.closest('.inputContainer, .checkboxContainer') || el).classList.add('moonbaseHidden');
        });
        var liveTvHeading = document.getElementById('DefaultLiveTvChannelSortBy');
        liveTvHeading = liveTvHeading && liveTvHeading.closest('.inputContainer').previousElementSibling;
        if (liveTvHeading && liveTvHeading.tagName === 'H4') liveTvHeading.classList.add('moonbaseHidden');

        // A tab left with nothing to show goes too.
        root.querySelectorAll('.moonfinTabPanel').forEach(function (panel) {
            if (panel.querySelector('.verticalSection:not(.moonbaseHidden)')) return;
            var tab = panel.getAttribute('data-tab');
            var nav = root.querySelector('.moonfinNavItem[data-tab="' + tab + '"]');
            if (nav) nav.classList.add('moonbaseHidden');
            try {
                if (localStorage.getItem('moonfinAdminActiveTab') === tab) localStorage.removeItem('moonfinAdminActiveTab');
            } catch (e) { /* the page opens on its first tab */ }
        });

        // Requests on Silo don't go through Moonbase, so they don't rely on sync.
        root.querySelectorAll('.moonfinChip').forEach(function (chip) {
            if (chip.textContent.trim() === 'Seerr requests') chip.classList.add('moonbaseHidden');
        });
        root.querySelectorAll('.moonfinNavSub').forEach(function (sub) {
            sub.textContent = sub.textContent.replace('Seerr, ', '');
        });

        // The Seerr options of the user defaults drive Silo's own requests.
        var defaults = root.querySelector('.moonfinTabPanel[data-tab="defaults"]');
        if (defaults) renameSeerr(defaults);
    }

    function renameSeerr(root) {
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        var node;
        while ((node = walker.nextNode())) {
            if (node.nodeValue.indexOf('Seerr') >= 0) node.nodeValue = node.nodeValue.replace(/Seerr/g, 'Requests');
        }
    }

    // Silo's own Home rows, by section type. Moonbase can't read a server's
    // section ids, so a row names its type and the app shows that profile's
    // first Silo row of the type. Continue watching and next up are left out
    // because Moonfin builds those rows itself.
    var siloRows = [
        ['next_in_series', 'Next in series'],
        ['watchlist', 'Watchlist'],
        ['favorites', 'Favorites'],
        ['recently_added', 'Recently added'],
        ['recently_released', 'New releases'],
        ['new_to_library', 'New this month'],
        ['returning_shows', 'Returning shows'],
        ['trending_on_server', 'Trending on this server'],
        ['most_watched', 'Most watched'],
        ['trending_discover', 'Trending worldwide'],
        ['profile_activity_feed', 'What others just watched'],
        ['recommended_for_you', 'Recommended for you'],
        ['because_you_watched', 'Because you watched'],
        ['similar_users_liked', 'Profiles like you enjoyed'],
        ['taste_match', 'Top picks today'],
        ['mood_collection', 'Mood picks'],
        ['seasonal_themed', 'Seasonal picks'],
        ['editorial_spotlight', 'Spotlight'],
        ['format_showcase', '4K & HDR showcase'],
        ['hidden_gems', 'Hidden gems'],
        ['critically_acclaimed', 'Critically acclaimed'],
        ['forgotten_favorites', 'Forgotten favorites'],
        ['genre_roulette', 'Genre roulette'],
        ['random', 'Surprise me'],
        ['short_watches', 'Short & sweet'],
        ['anniversaries', 'Anniversaries'],
        ['collection', 'A collection'],
        ['custom_filter', 'Titles matching rules'],
        ['admin_curated_list', 'Editor’s picks']
    ];

    // The page's row catalog and helpers are globals of its script, so the
    // Home rows builder is adjusted through them.
    function adaptHomeRows() {
        var liveTv = { livetv: true, onnow: true, activerecordings: true };
        for (var i = HOME_SECTION_DEFINITIONS.length - 1; i >= 0; i--) {
            var row = HOME_SECTION_DEFINITIONS[i];
            if (liveTv[row.type]) HOME_SECTION_DEFINITIONS.splice(i, 1);
            else if (row.type === 'seerr_watchlist') row.label = 'Requests Watchlist';
            else row.label = row.label.replace(/^Seerr /, '');
        }
        HOME_LAYOUT_TABS.forEach(function (tab) {
            if (tab.id === 'seerr') tab.label = 'Requests';
        });
        HOME_LAYOUT_TABS.splice(1, 0, { id: 'silo', label: 'Silo' });
        HOME_LAYOUT_STATE.available.silo = [];
        for (var j = BOTTOM_NAVBAR_TABS.length - 1; j >= 0; j--) {
            if (BOTTOM_NAVBAR_TABS[j][0] === 'liveTv') BOTTOM_NAVBAR_TABS.splice(j, 1);
            else BOTTOM_NAVBAR_TABS[j][1] = BOTTOM_NAVBAR_TABS[j][1].replace('Seerr', 'Requests');
        }

        var loadDynamicRows = window.loadHomeLayoutDynamicRows;
        window.loadHomeLayoutDynamicRows = function () {
            loadDynamicRows();
            setHomeLayoutAvailable('silo', siloRows.map(function (row) {
                return createDynamicCandidate('silo', 'server', 'type:' + row[0], '', row[1], 'Silo row', '');
            }));
        };
        var sectionMeta = window.homeSectionMeta;
        window.homeSectionMeta = function (section) {
            if (section && section.pluginSource === 'server') return 'Silo';
            var meta = sectionMeta(section);
            return meta === 'Seerr' ? 'Requests' : meta;
        };
        var builtinCandidate = window.createBuiltinCandidate;
        window.createBuiltinCandidate = function (definition) {
            var candidate = builtinCandidate(definition);
            if (candidate.meta === 'Seerr') candidate.meta = 'Requests';
            return candidate;
        };

        // Requests are always on in Silo, so the request defaults don't wait
        // for the hidden Seerr switch.
        for (var k = FIELD_DEPENDENCIES.length - 1; k >= 0; k--) {
            if (FIELD_DEPENDENCIES[k].parent === 'SeerrEnabled') FIELD_DEPENDENCIES.splice(k, 1);
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        var page = document.getElementById('MoonfinConfigPage');
        upgrade(document);
        renameServer(page);
        adaptSections(page);
        adaptHomeRows();
        addDefaultsLayerPicker(page);
        new MutationObserver(function (records) {
            records.forEach(function (record) { upgrade(record.target); });
        }).observe(page, { childList: true, subtree: true });

        page.dispatchEvent(new CustomEvent('pageshow', { bubbles: true }));
        bindRemoveUserData();
    });

    // Moonbase on Silo only: the single-user list also offers each of an
    // account's profiles, under the account. Picking the account still means
    // all of its profiles. The option shows the indented label, and its text
    // names the account too, for the page's messages.
    function addProfileOptions(users) {
        var select = document.getElementById('SingleUserSelect');
        if (!select) return;
        var current = select.value;
        select.querySelectorAll('option[data-profile]').forEach(function (o) { o.remove(); });
        (users || []).forEach(function (user) {
            var after = Array.prototype.find.call(select.options, function (o) { return o.value === user.Id; });
            (user.Profiles || []).forEach(function (profile) {
                if (!after) return;
                var option = document.createElement('option');
                option.value = profile.Id;
                option.dataset.profile = 'true';
                option.label = '\u00a0\u00a0\u00a0\u00a0\u21b3 ' + profile.Name;
                option.textContent = user.Name + ' \u203a ' + profile.Name;
                after.after(option);
                after = option;
            });
        });
        if (current) select.value = current;
    }

    // Moonbase on Silo only: removes the stored settings of the account, or
    // the one profile, picked in the page's single-user list.
    function bindRemoveUserData() {
        var select = document.getElementById('SingleUserSelect');
        var after = document.getElementById('ApplySingleUserResult');
        if (!select || !after) return;
        var help = document.createElement('div');
        help.className = 'fieldDescription';
        help.style.margin = '1.6em 0 0.4em';
        help.textContent = 'Or remove everything Moonbase keeps for the selected user, on every profile, or for just the selected profile. Use it for an account or profile deleted from Silo, since Silo doesn’t tell Moonbase about that. If they sign in again, they start from the defaults.';
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'raised emby-button';
        button.innerHTML = '<span>Remove Selected User’s Data</span>';
        var result = document.createElement('div');
        result.className = 'fieldDescription';
        result.style.cssText = 'display:none; margin-top:0.8em;';
        after.after(help, button, result);
        function show(text, failed) {
            result.style.display = '';
            result.style.color = failed ? '#d9534f' : '#52b54b';
            result.textContent = text;
        }
        button.addEventListener('click', function () {
            var option = select.selectedOptions[0];
            if (!option || !select.value) {
                show('Select a user first.', true);
                return;
            }
            var name = option.textContent;
            var target = select.value;
            var oneProfile = !!option.dataset.profile;
            if (!window.confirm('Remove all Moonbase data for ' + name + '?\n\nThis permanently erases their synced settings ' + (oneProfile ? 'on this profile.' : 'on every profile.'))) return;
            button.disabled = true;
            send('DELETE', getUrl('Moonfin/Admin/Users/' + encodeURIComponent(target))).then(json).then(function (body) {
                Array.prototype.slice.call(select.options).forEach(function (o) {
                    if (o.value === target || (!oneProfile && o.value.indexOf(target + '~') === 0)) o.remove();
                });
                show('Removed ' + name + ' (' + body.profilesRemoved + ' profile' + (body.profilesRemoved === 1 ? '' : 's') + ').');
            }, function (error) {
                show('Silo didn’t remove the data' + (error && error.status ? ' (status ' + error.status + ').' : '.'), true);
            }).finally(function () {
                button.disabled = false;
            });
        });
    }
})();
