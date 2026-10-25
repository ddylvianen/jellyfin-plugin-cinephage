// Cinephage Requests: adds a "Request via Cinephage" row to Jellyfin's search page.
(function () {
    'use strict';

    if (window.__cinephageRequests) {
        return;
    }
    window.__cinephageRequests = true;

    var SECTION_ID = 'cinephage-requests-section';
    var DEBOUNCE_MS = 700;

    var STATUS_LABELS = {
        Requested: 'Aangevraagd',
        Wanted: 'Wordt gezocht',
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

    function injectStyles() {
        if (document.getElementById('cinephage-requests-style')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'cinephage-requests-style';
        style.textContent = [
            '#' + SECTION_ID + ' { padding: 0 3.3% 2em; }',
            '#' + SECTION_ID + ' .cp-title { font-size: 1.4em; margin: 1em 0 .6em; }',
            '#' + SECTION_ID + ' .cp-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 1.2em; }',
            '#' + SECTION_ID + ' .cp-card { display: flex; flex-direction: column; gap: .4em; }',
            '#' + SECTION_ID + ' .cp-poster { aspect-ratio: 2/3; width: 100%; border-radius: .3em; background: rgba(128,128,128,.2) center/cover no-repeat; }',
            '#' + SECTION_ID + ' .cp-name { font-size: .95em; line-height: 1.25; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }',
            '#' + SECTION_ID + ' .cp-year { opacity: .6; }',
            '#' + SECTION_ID + ' .cp-btn { margin: 0; width: 100%; font-size: .9em; padding: .5em; }',
            '#' + SECTION_ID + ' .cp-badge { text-align: center; font-size: .85em; padding: .5em; border-radius: .3em; background: rgba(128,128,128,.25); }',
            '#' + SECTION_ID + ' .cp-badge.Available { background: rgba(46,160,67,.35); }',
            '#' + SECTION_ID + ' .cp-badge.Requested { background: rgba(0,164,220,.35); }',
            '#' + SECTION_ID + ' .cp-msg { opacity: .7; }',
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

    function renderCard(movie) {
        var poster = movie.PosterUrl ? ' style="background-image:url(\'' + escapeHtml(movie.PosterUrl) + '\')"' : '';
        var action = movie.Status === 'None'
            ? '<button is="emby-button" type="button" class="raised button-submit emby-button cp-btn" data-tmdb="' + movie.TmdbId + '">Aanvragen</button>'
            : '<div class="cp-badge ' + escapeHtml(movie.Status) + '">' + escapeHtml(STATUS_LABELS[movie.Status] || movie.Status) + '</div>';
        return '<div class="cp-card" title="' + escapeHtml(movie.Overview || '') + '">' +
            '<div class="cp-poster"' + poster + '></div>' +
            '<div class="cp-name">' + escapeHtml(movie.Title) + (movie.Year ? ' <span class="cp-year">(' + movie.Year + ')</span>' : '') + '</div>' +
            action + '</div>';
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
        if (section.parentNode !== container) {
            container.appendChild(section);
        }

        var body;
        if (state.loading) {
            body = '<div class="cp-msg">Zoeken in Cinephage…</div>';
        } else if (state.error) {
            body = '<div class="cp-msg">' + escapeHtml(state.error) + '</div>';
        } else if (!state.results.length) {
            body = '<div class="cp-msg">Geen films gevonden om aan te vragen.</div>';
        } else {
            body = '<div class="cp-grid">' + state.results.map(renderCard).join('') + '</div>';
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

    function requestMovie(button) {
        var client = api();
        var tmdbId = parseInt(button.getAttribute('data-tmdb'), 10);
        if (!client || !tmdbId) {
            return;
        }
        var movie = state.results.filter(function (m) { return m.TmdbId === tmdbId; })[0] || {};
        button.disabled = true;
        button.textContent = 'Bezig…';

        fetch(client.getUrl('Cinephage/Request'), {
            method: 'POST',
            headers: headers(client),
            body: JSON.stringify({ TmdbId: tmdbId, Title: movie.Title, Year: movie.Year })
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
                    state.results.forEach(function (m) {
                        if (m.TmdbId === tmdbId) {
                            m.Status = newStatus;
                        }
                    });
                    render();
                } else {
                    button.disabled = false;
                    button.textContent = 'Aanvragen';
                }
            })
            .catch(function () {
                toast('Aanvragen mislukt.');
                button.disabled = false;
                button.textContent = 'Aanvragen';
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
        // React may re-render the results container and drop our section; put it back.
        if (state.query && !document.getElementById(SECTION_ID)) {
            render();
        }
    }

    document.addEventListener('click', function (e) {
        var button = e.target.closest && e.target.closest('#' + SECTION_ID + ' .cp-btn');
        if (button) {
            e.preventDefault();
            e.stopPropagation();
            requestMovie(button);
        }
    }, true);

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
