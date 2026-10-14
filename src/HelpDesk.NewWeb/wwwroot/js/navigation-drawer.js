(function () {
    const registrations = new Map();
    let nextId = 1;

    window.helpdeskNavigationDrawer = {
        register: function (receiver) {
            const drawer = document.querySelector('[data-testid="app-navigation-drawer"]');
            const toggle = document.querySelector('[data-testid="navigation-toggle"]');
            if (!drawer || !toggle) return 0;
            const id = nextId++;
            let wasOpen = !drawer.classList.contains('mud-drawer--closed');
            let navigationClick = false;
            const overlayVisible = () => {
                const overlay = document.querySelector('.mud-overlay-drawer');
                if (!overlay) return false;
                const style = getComputedStyle(overlay);
                return style.visibility !== 'hidden' && style.pointerEvents !== 'none'
                    && overlay.getBoundingClientRect().width > 0;
            };
            const click = event => {
                navigationClick = event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey
                    && Boolean(event.target.closest('a[href]'));
            };
            const keydown = event => {
                if (event.key !== 'Escape' || !overlayVisible()) return;
                event.preventDefault();
                navigationClick = false;
                receiver.invokeMethodAsync('CloseNavigationFromShortcut').catch(() => {});
            };
            const observer = new MutationObserver(() => {
                const isOpen = !drawer.classList.contains('mud-drawer--closed');
                if (wasOpen && !isOpen && !navigationClick && window.matchMedia('(max-width: 1279px)').matches) {
                    toggle.focus({ preventScroll: true });
                }
                if (isOpen !== wasOpen) navigationClick = false;
                wasOpen = isOpen;
            });
            drawer.addEventListener('click', click);
            document.addEventListener('keydown', keydown);
            observer.observe(drawer, { attributes: true, attributeFilter: ['class'] });
            registrations.set(id, { drawer, click, keydown, observer });
            return id;
        },
        dispose: function (id) {
            const registration = registrations.get(id);
            if (!registration) return;
            registration.drawer.removeEventListener('click', registration.click);
            document.removeEventListener('keydown', registration.keydown);
            registration.observer.disconnect();
            registrations.delete(id);
        }
    };
}());
