// Cinephage Requests: adds a "Request via Cinephage" section to Jellyfin's search page.
(function () {
    'use strict';

    if (window.__cinephageRequests) {
        return;
    }
    window.__cinephageRequests = true;

    var SECTION_ID = 'cinephage-requests-section';
    var MODAL_ID = 'cinephage-season-modal';
    var DEBOUNCE_MS = 700;

    var STATUS_LABELS = {
        Requested: 'Aangevraagd',
        Wanted: 'Wordt gezocht',
        Partial: 'Deels beschikbaar',
        Available: 'Beschikbaar'
    };

    var state = { query: '', results: [], loading: false, error: null, timer: null, seq: 0 };

    function api() {
        var client = window.ApiClient;
        return client && client.accessToken && client.accessToken() && client.getCurrentUserId && client.getCurrentUserId() ? client : null;
    }

    function headers(client) {
        // Jellyfin 12 ignores X-Emby-Token by default; use the MediaBrowser scheme.
        return {
            'Authorization': 'MediaBrowser Token="' + client.accessToken() + '"',
            'Accept': 'application/json',
            'Content-Type': 'application/json'
        };
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function findItem(type, tmdbId) {
        return state.results.filter(function (m) { return m.MediaType === type && m.TmdbId === tmdbId; })[0];
    }

    function injectStyles() {
        if (document.getElementById('cinephage-requests-style')) {
            return;
        }
        var s = '#' + SECTION_ID;
        var m = '#' + MODAL_ID;
        var style = document.createElement('style');
        style.id = 'cinephage-requests-style';
        style.textContent = [
            s + ' { padding: 0 3.3% 2em; }',
            s + ' .cp-title { font-size: 1.4em; margin: 1em 0 .6em; }',
            s + ' .cp-subtitle { font-size: 1.1em; margin: 1.2em 0 .6em; opacity: .85; }',
            s + ' .cp-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 1.2em; }',
            s + ' .cp-card { display: flex; flex-direction: column; gap: .4em; }',
            s + ' .cp-poster { position: relative; aspect-ratio: 2/3; width: 100%; border-radius: .3em; background: rgba(128,128,128,.2) center/cover no-repeat; }',
            s + ' .cp-type { position: absolute; top: .4em; left: .4em; font-size: .75em; font-weight: 600; padding: .2em .55em; border-radius: .3em; color: #fff; background: rgba(0,0,0,.7); }',
            s + ' .cp-type.series { background: rgba(170,92,195,.9); }',
            s + ' .cp-type.movie { background: rgba(0,164,220,.9); }',
            s + ' .cp-name { font-size: .95em; line-height: 1.25; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }',
            s + ' .cp-year, ' + s + ' .cp-hint { opacity: .6; }',
            s + ' .cp-hint { font-size: .8em; }',
            s + ' .cp-btn { margin: 0; width: 100%; font-size: .9em; padding: .5em; }',
            s + ' .cp-badge { text-align: center; font-size: .85em; padding: .5em; border-radius: .3em; background: rgba(128,128,128,.25); }',
            s + ' .cp-badge.Available { background: rgba(46,160,67,.35); }',
            s + ' .cp-badge.Requested { background: rgba(0,164,220,.35); }',
            s + ' .cp-msg { opacity: .7; }',
            m + ' { position: fixed; inset: 0; z-index: 99998; background: rgba(0,0,0,.6); display: flex; align-items: center; justify-content: center; padding: 1em; }',
            m + ' .cp-dialog { background: #202020; color: #fff; border-radius: .5em; width: 100%; max-width: 30em; max-height: 85vh; display: flex; flex-direction: column; box-shadow: 0 8px 32px rgba(0,0,0,.5); }',
            m + ' .cp-dialog h3 { margin: 0; padding: 1em 1.2em .4em; font-size: 1.2em; }',
            m + ' .cp-seasons { overflow-y: auto; padding: .4em 1.2em; }',
            m + ' .cp-season { display: flex; align-items: center; gap: .8em; padding: .55em 0; border-bottom: 1px solid rgba(255,255,255,.08); cursor: pointer; }',
            m + ' .cp-season.disabled { opacity: .5; cursor: default; }',
            m + ' .cp-season input { width: 1.2em; height: 1.2em; flex: none; }',
            m + ' .cp-season .cp-meta { margin-left: auto; font-size: .85em; opacity: .7; text-align: right; }',
            m + ' .cp-actions { display: flex; gap: .6em; justify-content: flex-end; align-items: center; padding: 1em 1.2em; flex-wrap: wrap; }',
            m + ' .cp-actions .cp-all { margin-right: auto; }',
            m + ' .cp-actions button { margin: 0; }',
            '.cp-toast { position: fixed; left: 50%; bottom: 2em; transform: translateX(-50%); z-index: 99999; background: #222; color: #fff; padding: .8em 1.2em; border-radius: .4em; box-shadow: 0 4px 16px rgba(0,0,0,.4); max-width: 90vw; }'
        ].join('\n');
        document.head.appendChild(style);
    }

    function toast(message) {
        var el = document.createElement('div');
        el.className = 'cp-toast';
        el.textContent = message;
        document.body.appendChild(el);
        setTimeout(function () { el.remove(); }, 4000);
    }

    function searchPage() {
        var page = document.querySelector('#searchPage:not(.hide)');
        return page && page.querySelector('#searchTextInput') ? page : null;
    }

    function renderCard(item) {
        var poster = item.PosterUrl ? ' style="background-image:url(\'' + escapeHtml(item.PosterUrl) + '\')"' : '';
        var data = ' data-type="' + item.MediaType + '" data-tmdb="' + item.TmdbId + '"';
        var action;
        if (item.MediaType === 'series') {
            // Series can always get more seasons; the dialog shows what is still requestable.
            action = (item.Status !== 'None' ? '<div class="cp-hint">' + escapeHtml(STATUS_LABELS[item.Status] || item.Status) + '</div>' : '') +
                '<button is="emby-button" type="button" class="raised button-submit emby-button cp-btn"' + data + '>Seizoenen kiezen</button>';
        } else if (item.Status === 'None') {
            action = '<button is="emby-button" type="button" class="raised button-submit emby-button cp-btn"' + data + '>Aanvragen</button>';
        } else {
            action = '<div class="cp-badge ' + escapeHtml(item.Status) + '">' + escapeHtml(STATUS_LABELS[item.Status] || item.Status) + '</div>';
        }
        return '<div class="cp-card" title="' + escapeHtml(item.Overview || '') + '">' +
            '<div class="cp-poster"' + poster + '><span class="cp-type ' + item.MediaType + '">' + (item.MediaType === 'series' ? 'Serie' : 'Film') + '</span></div>' +
            '<div class="cp-name">' + escapeHtml(item.Title) + (item.Year ? ' <span class="cp-year">(' + item.Year + ')</span>' : '') + '</div>' +
            action + '</div>';
    }

    function renderGroup(title, items) {
        if (!items.length) {
            return '';
        }
        return '<h3 class="cp-subtitle">' + title + '</h3><div class="cp-grid">' + items.map(renderCard).join('') + '</div>';
    }

    // Keeps the section directly under the search box, above Jellyfin's own results.
    function placeAtTop(container, section) {
        if (section.parentNode !== container || container.firstChild !== section) {
            container.insertBefore(section, container.firstChild);
        }
    }

    function render() {
        var page = searchPage();
        var existing = document.getElementById(SECTION_ID);
        if (!page || !state.query) {
            if (existing) {
                existing.remove();
            }
            return;
        }

        var container = page.querySelector('.searchResults') || page;
        var section = existing;
        if (!section) {
            section = document.createElement('div');
            section.id = SECTION_ID;
            section.className = 'verticalSection';
        }
        placeAtTop(container, section);

        var body;
        if (state.loading) {
            body = '<div class="cp-msg">Zoeken in Cinephage…</div>';
        } else if (state.error) {
            body = '<div class="cp-msg">' + escapeHtml(state.error) + '</div>';
        } else if (!state.results.length) {
            body = '<div class="cp-msg">Niets gevonden om aan te vragen.</div>';
        } else {
            body = renderGroup('Films', state.results.filter(function (m) { return m.MediaType === 'movie'; })) +
                renderGroup('Series', state.results.filter(function (m) { return m.MediaType === 'series'; }));
        }
        section.innerHTML = '<h2 class="sectionTitle cp-title">Aanvragen via Cinephage</h2>' + body;

        // Jellyfin's "no results" message is misleading when Cinephage did find something.
        var noItems = page.querySelector('.noItemsMessage');
        if (noItems) {
            noItems.style.display = state.results.length && !state.loading ? 'none' : '';
        }
    }

    function search(query) {
        var client = api();
        if (!client) {
            return;
        }
        var seq = ++state.seq;
        state.query = query;
        state.loading = true;
        state.error = null;
        render();

        fetch(client.getUrl('Cinephage/Search', { query: query }), { headers: headers(client) })
            .then(function (response) {
                return response.json().then(function (data) {
                    if (!response.ok) {
                        throw new Error((data && data.Message) || 'Cinephage is niet bereikbaar.');
                    }
                    return data;
                });
            })
            .then(function (results) {
                if (seq !== state.seq) {
                    return;
                }
                state.results = results || [];
                state.loading = false;
                render();
            })
            .catch(function (err) {
                if (seq !== state.seq) {
                    return;
                }
                state.loading = false;
                state.error = err.message || 'Zoeken in Cinephage mislukt.';
                render();
            });
    }

    function onQueryChanged(value) {
        var query = (value || '').trim();
        clearTimeout(state.timer);
        if (query.length < 2) {
            state.seq++;
            state.query = '';
            state.results = [];
            render();
            return;
        }
        if (query === state.query) {
            render();
            return;
        }
        state.timer = setTimeout(function () { search(query); }, DEBOUNCE_MS);
    }

    // Sends a request; resolves with the outcome and updates the card status.
    function submitRequest(item, seasons) {
        var client = api();
        return fetch(client.getUrl('Cinephage/Request'), {
            method: 'POST',
            headers: headers(client),
            body: JSON.stringify({ MediaType: item.MediaType, TmdbId: item.TmdbId, Seasons: seasons, Title: item.Title, Year: item.Year })
        })
            .then(function (response) { return response.json(); })
            .then(function (outcome) {
                toast(outcome.Message || (outcome.Success ? 'Aangevraagd.' : 'Aanvragen mislukt.'));
                // Cinephage error codes that still mean "nothing left to request".
                var newStatus = outcome.Success || outcome.Code === 'duplicate_request' ? 'Requested'
                    : outcome.Code === 'already_available' ? 'Available'
                    : outcome.Code === 'already_in_library' ? 'Wanted'
                    : null;
                if (newStatus) {
                    item.Status = newStatus;
                    render();
                }
                return outcome;
            })
            .catch(function () {
                toast('Aanvragen mislukt.');
                return { Success: false };
            });
    }

    function requestMovie(button, item) {
        button.disabled = true;
        button.textContent = 'Bezig…';
        submitRequest(item, null).then(function (outcome) {
            if (!outcome.Success && document.body.contains(button)) {
                button.disabled = false;
                button.textContent = 'Aanvragen';
            }
        });
    }

    function closeModal() {
        var modal = document.getElementById(MODAL_ID);
        if (modal) {
            modal.remove();
        }
    }

    function seasonMeta(season) {
        if (season.Status === 'Requested') {
            return 'Aangevraagd';
        }
        if (season.Status === 'Available') {
            return 'Beschikbaar';
        }
        if (!season.EpisodeCount) {
            return 'Nog geen afleveringen';
        }
        var meta = season.EpisodeCount + ' afl.';
        if (season.AvailableCount) {
            meta += ' · ' + season.AvailableCount + ' beschikbaar';
        }
        if (season.AirDate) {
            meta += ' · ' + season.AirDate.substring(0, 4);
        }
        return meta;
    }

    function openSeasonPicker(item) {
        var client = api();
        if (!client) {
            return;
        }
        closeModal();
        var modal = document.createElement('div');
        modal.id = MODAL_ID;
        modal.innerHTML = '<div class="cp-dialog" role="dialog" aria-modal="true">' +
            '<h3>' + escapeHtml(item.Title) + (item.Year ? ' (' + item.Year + ')' : '') + '</h3>' +
            '<div class="cp-seasons"><div class="cp-msg">Seizoenen laden…</div></div>' +
            '<div class="cp-actions">' +
            '<button is="emby-button" type="button" class="emby-button cp-all" disabled>Alles selecteren</button>' +
            '<button is="emby-button" type="button" class="emby-button cp-cancel">Annuleren</button>' +
            '<button is="emby-button" type="button" class="raised button-submit emby-button cp-submit" disabled>Aanvragen</button>' +
            '</div></div>';
        document.body.appendChild(modal);

        var list = modal.querySelector('.cp-seasons');
        var submit = modal.querySelector('.cp-submit');
        var all = modal.querySelector('.cp-all');

        function checkboxes() {
            return Array.prototype.slice.call(list.querySelectorAll('input:not(:disabled)'));
        }

        function update() {
            var boxes = checkboxes();
            var count = boxes.filter(function (b) { return b.checked; }).length;
            submit.disabled = count === 0;
            submit.textContent = count ? 'Aanvragen (' + count + ')' : 'Aanvragen';
            all.disabled = boxes.length === 0;
            all.textContent = boxes.length && count === boxes.length ? 'Niets selecteren' : 'Alles selecteren';
        }

        modal.addEventListener('click', function (e) {
            if (e.target === modal || e.target.closest('.cp-cancel')) {
                closeModal();
            }
        });
        list.addEventListener('change', update);
        all.addEventListener('click', function () {
            var boxes = checkboxes();
            var select = !boxes.every(function (b) { return b.checked; });
            boxes.forEach(function (b) { b.checked = select; });
            update();
        });
        submit.addEventListener('click', function () {
            var seasons = checkboxes().filter(function (b) { return b.checked; }).map(function (b) { return parseInt(b.value, 10); });
            submit.disabled = true;
            submit.textContent = 'Bezig…';
            submitRequest(item, seasons).then(function (outcome) {
                if (outcome.Success) {
                    closeModal();
                } else {
                    update();
                }
            });
        });

        fetch(client.getUrl('Cinephage/Series/' + item.TmdbId + '/Seasons'), { headers: headers(client) })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error();
                }
                return response.json();
            })
            .then(function (seasons) {
                if (!seasons.length) {
                    list.innerHTML = '<div class="cp-msg">Geen seizoenen gevonden.</div>';
                    return;
                }
                list.innerHTML = seasons.map(function (season) {
                    var disabled = !season.Requestable;
                    return '<label class="cp-season' + (disabled ? ' disabled' : '') + '">' +
                        '<input type="checkbox" value="' + season.Number + '"' + (disabled ? ' disabled' : '') + '>' +
                        '<span>' + escapeHtml(season.Name) + '</span>' +
                        '<span class="cp-meta">' + escapeHtml(seasonMeta(season)) + '</span></label>';
                }).join('');
                update();
            })
            .catch(function () {
                list.innerHTML = '<div class="cp-msg">Seizoenen ophalen mislukt.</div>';
            });
    }

    function attach() {
        var page = searchPage();
        if (!page) {
            if (document.getElementById(SECTION_ID)) {
                render();
            }
            return;
        }
        var input = page.querySelector('#searchTextInput');
        if (!input.dataset.cinephageListener) {
            input.dataset.cinephageListener = '1';
            input.addEventListener('input', function () { onQueryChanged(input.value); });
            onQueryChanged(input.value);
        }
        // React may re-render the results container and drop or push down our section; put it back on top.
        var section = document.getElementById(SECTION_ID);
        if (state.query && !section) {
            render();
        } else if (section) {
            placeAtTop(page.querySelector('.searchResults') || page, section);
        }
    }

    document.addEventListener('click', function (e) {
        var button = e.target.closest && e.target.closest('#' + SECTION_ID + ' .cp-btn');
        if (!button) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        var item = findItem(button.getAttribute('data-type'), parseInt(button.getAttribute('data-tmdb'), 10));
        if (!item || !api()) {
            return;
        }
        if (item.MediaType === 'series') {
            openSeasonPicker(item);
        } else {
            requestMovie(button, item);
        }
    }, true);

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && document.getElementById(MODAL_ID)) {
            closeModal();
        }
    });

    var scheduled = false;
    new MutationObserver(function () {
        if (scheduled) {
            return;
        }
        scheduled = true;
        requestAnimationFrame(function () {
            scheduled = false;
            attach();
        });
    }).observe(document.body, { childList: true, subtree: true });

    injectStyles();
    attach();
})();
