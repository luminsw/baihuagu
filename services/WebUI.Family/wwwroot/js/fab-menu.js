// 移动端侧边栏：导航后自动关闭
(function() {
    var observer = new MutationObserver(function() {
        var sidebar = document.querySelector('.sidebar');
        if (sidebar && sidebar.classList.contains('open') && window.innerWidth <= 768) {
            var links = sidebar.querySelectorAll('a[href]');
            links.forEach(function(link) {
                link.removeEventListener('click', closeSidebar);
                link.addEventListener('click', closeSidebar);
            });
        }
    });
    function closeSidebar() {
        var sidebar = document.querySelector('.sidebar');
        if (sidebar) sidebar.classList.remove('open');
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function() {
            var sidebar = document.querySelector('.sidebar');
            if (sidebar) {
                sidebar.querySelectorAll('a[href]').forEach(function(link) {
                    link.addEventListener('click', closeSidebar);
                });
            }
            observer.observe(document.body, { childList: true, subtree: true });
        });
    } else {
        observer.observe(document.body, { childList: true, subtree: true });
    }
})();

// FAB 系统菜单
(function() {
    function initFab() {
        var btn = document.getElementById('fab-btn');
        var menu = document.getElementById('fab-menu');
        if (!btn || !menu || btn.dataset.fabInit) return;
        btn.dataset.fabInit = '1';
        btn.addEventListener('click', function(e) {
            e.stopPropagation();
            var isOpen = menu.style.display !== 'none';
            menu.style.display = isOpen ? 'none' : 'block';
            btn.classList.toggle('active', !isOpen);
        });
        document.addEventListener('click', function(e) {
            var container = document.getElementById('fab-container');
            if (container && !container.contains(e.target)) {
                menu.style.display = 'none';
                btn.classList.remove('active');
            }
        });
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function() { initFab(); });
    } else {
        initFab();
    }
    var fabObserver = new MutationObserver(function() { initFab(); });
    fabObserver.observe(document.body, { childList: true, subtree: true });
})();