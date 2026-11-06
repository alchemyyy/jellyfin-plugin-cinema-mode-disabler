(function () {
    // Jellyfin Web keeps Cinema Mode per browser and user in localStorage['{userId}-enableCinemaMode'], true when unset
    function disableCinemaMode() {
        try {
            var credentials = JSON.parse(window.localStorage.getItem('jellyfin_credentials') || '{}');
            var servers = credentials && credentials.Servers;
            if (!Array.isArray(servers)) {
                return;
            }
            for (var i = 0; i < servers.length; i++) {
                var userId = servers[i] && servers[i].UserId;
                if (typeof userId === 'string' && userId) {
                    window.localStorage.setItem(userId + '-enableCinemaMode', 'false');
                }
            }
        } catch (e) {
            // Blocked storage or malformed credentials leave Jellyfin Web as it is
        }
    }

    disableCinemaMode();
    // Logging in and opening an item both change the hash before any play starts
    window.addEventListener('hashchange', disableCinemaMode);
})();
