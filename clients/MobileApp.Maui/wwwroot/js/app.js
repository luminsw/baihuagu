window.scrollIntoView = function (element) {
    if (element && element.scrollIntoView) {
        element.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
};
