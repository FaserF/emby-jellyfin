(function () {
    "use strict";
    console.log("[Emby Federation Plugin] Client script active");

    function injectTopEmbyButton() {
        var headerRight = document.querySelector(".headerRight");
        if (!headerRight || document.getElementById("embyFederationHubBtn")) return;

        var btn = document.createElement("button");
        btn.id = "embyFederationHubBtn";
        btn.type = "button";
        btn.className = "paper-icon-button-light";
        btn.title = "Emby Federation Hub — Catalog Search, Stream Bridge & Account Link";
        btn.style.cssText = "color:#52B54B;font-weight:bold;font-size:13px;padding:4px 10px;border:1px solid #52B54B;border-radius:6px;margin-right:8px;cursor:pointer;background:rgba(82,181,75,0.12);";
        btn.textContent = "🟢 Emby Hub";
        btn.onclick = openEmbySearchModal;
        headerRight.insertBefore(btn, headerRight.firstChild);
    }

    function openEmbySearchModal() {
        var existing = document.getElementById("embyFederationModal");
        if (existing) existing.remove();

        var modal = document.createElement("div");
        modal.id = "embyFederationModal";
        modal.style.cssText = "position:fixed;inset:0;background:rgba(0,0,0,0.92);z-index:99999;display:flex;flex-direction:column;padding:24px;overflow-y:auto;color:#fff;font-family:sans-serif;";
        modal.innerHTML =
            '<div style="max-width:1040px;width:100%;margin:0 auto;">' +
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:16px;flex-wrap:wrap;gap:10px;">' +
            '<h2 style="margin:0;color:#52B54B;">🟢 Emby Federation &amp; Stream Bridge Hub</h2>' +
            '<div style="display:flex;gap:8px;align-items:center;">' +
            '<button id="embyBtnToggleAccount" style="background:#27272a;color:#fff;border:1px solid #444;padding:8px 14px;border-radius:6px;cursor:pointer;font-size:13px;">👤 My Emby Account</button>' +
            '<button id="embyBtnTriggerSyncModal" style="background:#27272a;color:#fff;border:1px solid #52B54B;padding:8px 14px;border-radius:6px;cursor:pointer;font-size:13px;">⚡ Sync Library</button>' +
            '<button id="embyModalClose" style="background:#333;color:#fff;border:none;padding:8px 16px;border-radius:6px;cursor:pointer;">✕ Close</button>' +
            '</div></div>' +

            '<div id="embyAccountPanel" style="display:none;background:#18181b;border:1px solid #52B54B;border-radius:8px;padding:14px;margin-bottom:16px;">' +
            '<h3 style="margin-top:0;color:#52B54B;">Link Personal Emby Account</h3>' +
            '<div style="display:flex;gap:10px;flex-wrap:wrap;margin-bottom:10px;">' +
            '<input id="embyModalServerUrl" type="url" placeholder="https://emby.example.com" style="flex:1;min-width:200px;padding:8px 12px;border-radius:4px;border:1px solid #444;background:#111;color:#fff;" />' +
            '<input id="embyModalUsername" type="text" placeholder="Username" style="flex:1;min-width:140px;padding:8px 12px;border-radius:4px;border:1px solid #444;background:#111;color:#fff;" />' +
            '<input id="embyModalPassword" type="password" placeholder="Password" style="flex:1;min-width:140px;padding:8px 12px;border-radius:4px;border:1px solid #444;background:#111;color:#fff;" />' +
            '<button id="embyModalSaveAccount" style="background:#52B54B;color:#fff;border:none;padding:8px 16px;border-radius:4px;font-weight:bold;cursor:pointer;">Link Account</button>' +
            '</div>' +
            '<div id="embyModalAccountStatus" style="font-size:13px;color:#aaa;">Enter your personal credentials to map your Jellyfin watch history and favorites.</div>' +
            '</div>' +

            '<div style="display:flex;gap:10px;margin-bottom:16px;flex-wrap:wrap;">' +
            '<select id="embySearchType" style="padding:10px 12px;border-radius:6px;border:1px solid #444;background:#18181b;color:#fff;font-size:14px;">' +
            '<option value="">All Media</option>' +
            '<option value="Movie">Movies</option>' +
            '<option value="Series">TV Series</option>' +
            '<option value="Episode">Episodes</option>' +
            '</select>' +
            '<input id="embySearchInput" type="text" placeholder="Search federated Emby catalog (Title, actor, genre)..." style="flex:1;min-width:220px;padding:10px 14px;border-radius:6px;border:1px solid #444;background:#18181b;color:#fff;font-size:15px;" />' +
            '<button id="embySearchGo" style="background:#52B54B;color:#fff;border:none;padding:10px 20px;border-radius:6px;font-weight:bold;cursor:pointer;">Search</button>' +
            '</div>' +

            '<div id="embyStatusBanner" style="font-size:13px;color:#888;margin-bottom:12px;">Checking connection...</div>' +
            '<div id="embySearchResults" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:16px;"></div>' +
            '</div>';

        document.body.appendChild(modal);

        document.getElementById("embyModalClose").onclick = function () {
            modal.remove();
        };

        var accountPanel = document.getElementById("embyAccountPanel");
        document.getElementById("embyBtnToggleAccount").onclick = function () {
            accountPanel.style.display = accountPanel.style.display === "none" ? "block" : "none";
        };

        // Fetch status
        fetch("/api/embyPlugin/status")
            .then(function (r) { return r.json(); })
            .then(function (status) {
                var banner = document.getElementById("embyStatusBanner");
                if (status.connected) {
                    banner.innerHTML = '<span style="color:#52B54B;">● Remote Emby Server Connected:</span> ' + (status.serverUrl || "") + ' | Catalog: ' + (status.moviesTotal || 0) + ' Movies, ' + (status.seriesTotal || 0) + ' Series (' + (status.episodesTotal || 0) + ' Episodes)';
                } else {
                    banner.innerHTML = '<span style="color:#e50914;">○ Emby Server not connected or credentials required.</span>';
                }
            })
            .catch(function (e) {
                console.warn("[Emby Hub] Could not get status", e);
            });

        // Trigger Sync
        document.getElementById("embyBtnTriggerSyncModal").onclick = function () {
            var banner = document.getElementById("embyStatusBanner");
            banner.textContent = "⚡ Syncing Emby catalog into Jellyfin...";
            fetch("/api/embyPlugin/sync", { method: "POST" })
                .then(function (r) { return r.json(); })
                .then(function (res) {
                    banner.innerHTML = '<span style="color:#52B54B;">✓ Sync finished!</span> Movies: ' + (res.moviesAdded || 0) + ', Episodes: ' + (res.episodesAdded || 0);
                })
                .catch(function (err) {
                    banner.textContent = "✗ Sync failed: " + err.message;
                });
        };

        // Link Account
        document.getElementById("embyModalSaveAccount").onclick = function () {
            var sUrl = document.getElementById("embyModalServerUrl").value.trim();
            var uName = document.getElementById("embyModalUsername").value.trim();
            var pWord = document.getElementById("embyModalPassword").value;
            var stat = document.getElementById("embyModalAccountStatus");

            if (!sUrl || !uName) {
                stat.textContent = "Please enter server URL and username.";
                return;
            }

            stat.textContent = "Authenticating with Emby...";
            var currentUser = (window.ApiClient && ApiClient.getCurrentUserId) ? ApiClient.getCurrentUserId() : "";

            fetch("/api/embyPlugin/auth/test", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    serverUrl: sUrl,
                    username: uName,
                    password: pWord,
                    jellyfinUserId: currentUser
                })
            }).then(function (r) { return r.json(); })
            .then(function (res) {
                if (res.ok) {
                    stat.style.color = "#52B54B";
                    stat.textContent = "✓ Linked successfully as " + res.embyUser + "!";
                } else {
                    stat.style.color = "#e50914";
                    stat.textContent = "✗ Auth failed: " + (res.error || "Check credentials");
                }
            }).catch(function (err) {
                stat.style.color = "#e50914";
                stat.textContent = "✗ Error: " + err.message;
            });
        };

        // Search
        function performSearch() {
            var query = document.getElementById("embySearchInput").value.trim();
            var type = document.getElementById("embySearchType").value;
            var resultsEl = document.getElementById("embySearchResults");
            if (!query) return;

            resultsEl.innerHTML = '<div style="color:#aaa;">Searching remote Emby catalog...</div>';
            var url = "/api/embyPlugin/search?q=" + encodeURIComponent(query);
            if (type) url += "&type=" + encodeURIComponent(type);

            fetch(url)
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    resultsEl.innerHTML = "";
                    var items = (data && data.items) ? data.items : [];
                    if (items.length === 0) {
                        resultsEl.innerHTML = '<div style="color:#aaa;">No matches found on remote Emby server.</div>';
                        return;
                    }

                    items.forEach(function (item) {
                        var card = document.createElement("div");
                        card.style.cssText = "background:#18181b;border:1px solid #333;border-radius:8px;overflow:hidden;display:flex;flex-direction:column;";
                        var thumbUrl = item.thumbUrl || item.backdropUrl || "";
                        var imgHtml = thumbUrl
                            ? '<img src="' + thumbUrl + '" style="width:100%;height:140px;object-fit:cover;background:#111;" />'
                            : '<div style="height:140px;background:#222;display:flex;align-items:center;justify-content:center;color:#555;">No Poster</div>';

                        var playUrl = item.directStreamUrl || ("/api/embyPlugin/stream/" + item.id);
                        card.innerHTML =
                            imgHtml +
                            '<div style="padding:10px;flex:1;display:flex;flex-direction:column;justify-content:space-between;">' +
                            '<div>' +
                            '<div style="font-weight:bold;font-size:14px;margin-bottom:4px;color:#fff;">' + escapeHtml(item.name) + '</div>' +
                            '<div style="font-size:12px;color:#888;margin-bottom:8px;">' + (item.type || "") + ' • ' + (item.productionYear || "") + ' • ' + (item.container || "mkv") + '</div>' +
                            '</div>' +
                            '<div style="display:flex;gap:6px;">' +
                            '<a href="' + playUrl + '" target="_blank" style="flex:1;text-align:center;background:#52B54B;color:#fff;text-decoration:none;padding:6px;border-radius:4px;font-size:12px;font-weight:bold;">▶ Stream</a>' +
                            (item.embyWebUrl ? '<a href="' + item.embyWebUrl + '" target="_blank" style="padding:6px 10px;background:#27272a;color:#fff;text-decoration:none;border-radius:4px;font-size:12px;">Emby</a>' : '') +
                            '</div></div>';
                        resultsEl.appendChild(card);
                    });
                })
                .catch(function (err) {
                    resultsEl.innerHTML = '<div style="color:#e50914;">Search error: ' + err.message + '</div>';
                });
        }

        document.getElementById("embySearchGo").onclick = performSearch;
        document.getElementById("embySearchInput").onkeydown = function (e) {
            if (e.key === "Enter") performSearch();
        };
    }

    function escapeHtml(str) {
        if (!str) return "";
        return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
    }

    // Try injecting button on page load and navigation
    if (document.readyState === "complete" || document.readyState === "interactive") {
        injectTopEmbyButton();
    } else {
        document.addEventListener("DOMContentLoaded", injectTopEmbyButton);
    }
    window.addEventListener("viewshow", injectTopEmbyButton);
    setInterval(injectTopEmbyButton, 3000);
})();
