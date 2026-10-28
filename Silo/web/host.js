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
            var draft = draftOffered ? null : readDraft();
            draftOffered = true;
            if (draft) {
                lastConfig = draft;
                notice('Restored the changes you hadn’t saved before your Silo session for this page ended. Press Save to keep them.');
                return Promise.resolve(clone(draft));
            }
            return this.getJSON(getUrl('Moonfin/Admin/Configuration')).then(function (config) {
                lastConfig = config;
                return clone(config);
            }, function (error) {
                // Saving reads the configuration first. Once the launch cookie
                // has run out, carry on from the copy already loaded so the
                // save fails on its own request and keeps the changes.
                var status = error && error.status;
                if (status === 401 && lastConfig) return clone(lastConfig);
                // The page has no failure handler of its own, so its loading
                // overlay would stay up over the settings.
                Dashboard.hideLoadingMsg();
                if (status !== 401) toast('Silo didn’t load the settings' + (status ? ' (status ' + status + ').' : '.'));
                throw error;
            });
        },
        updatePluginConfiguration: function (id, config) {
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
            return this.getJSON(getUrl('Moonfin/Admin/Users'));
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

    // Notes for the sections Silo can't fully support. Their settings still
    // save, so nothing is lost.
    var unsupported = {
        'Connecting to this server': 'Moonbase on Silo doesn’t host Moonfin web, so these settings do nothing on Silo.',
        'Seerr Integration': 'Silo has its own requests feature, and the Seerr proxy isn’t part of Moonbase on Silo. These settings do nothing on Silo.',
        'IMDb Charts': 'Not available on Silo. These settings do nothing on Silo.',
        'Recommendations': 'Silo doesn’t let plugins read its library, so Moonfin uses Silo’s own recommendations instead.',
        'Studio Logos': 'Not available on Silo. These settings do nothing on Silo.',
        'Anime Filler & Recap Markers': 'Silo doesn’t let plugins read its library, so this isn’t available on Silo.',
        'Retro Games (EmulatorJS)': 'Silo doesn’t let plugins read game files, so this isn’t available on Silo.',
        'Uploaded Themes': 'Custom themes aren’t available on Silo.',
        'Web Plugin Status': 'Silo’s own web app can’t load Moonfin add-ons, so this doesn’t apply on Silo.',
        'Active Downloads': 'Silo doesn’t share its transcodes with plugins, so this isn’t available on Silo.',
        'MDBList Integration': 'Ratings work on Silo. The official MDBList lists don’t.'
    };

    function markUnsupported(root) {
        root.querySelectorAll('.sectionTitle').forEach(function (title) {
            var text = unsupported[title.textContent.trim()];
            if (!text) return;
            var note = document.createElement('div');
            note.className = 'moonbaseSiloNote';
            note.textContent = text;
            title.insertAdjacentElement('afterend', note);
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        var page = document.getElementById('MoonfinConfigPage');
        upgrade(document);
        renameServer(page);
        markUnsupported(page);
        new MutationObserver(function (records) {
            records.forEach(function (record) { upgrade(record.target); });
        }).observe(page, { childList: true, subtree: true });

        page.dispatchEvent(new CustomEvent('pageshow', { bubbles: true }));
        bindRemoveUserData();
    });

    // Moonbase on Silo only: removes the stored settings of the account
    // picked in the page's single-user list.
    function bindRemoveUserData() {
        var select = document.getElementById('SingleUserSelect');
        var after = document.getElementById('ApplySingleUserResult');
        if (!select || !after) return;
        var help = document.createElement('div');
        help.className = 'fieldDescription';
        help.style.margin = '1.6em 0 0.4em';
        help.textContent = 'Or remove everything Moonbase keeps for the selected user, on every profile. Use it for an account deleted from Silo, since Silo doesn’t tell Moonbase about that. If the user signs in again, they start from the defaults.';
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
            if (!window.confirm('Remove all Moonbase data for ' + name + '?\n\nThis permanently erases their synced settings on every profile.')) return;
            button.disabled = true;
            send('DELETE', getUrl('Moonfin/Admin/Users/' + encodeURIComponent(select.value))).then(json).then(function (body) {
                option.remove();
                show('Removed ' + name + ' (' + body.profilesRemoved + ' profile' + (body.profilesRemoved === 1 ? '' : 's') + ').');
            }, function (error) {
                show('Silo didn’t remove the data' + (error && error.status ? ' (status ' + error.status + ').' : '.'), true);
            }).finally(function () {
                button.disabled = false;
            });
        });
    }
})();
