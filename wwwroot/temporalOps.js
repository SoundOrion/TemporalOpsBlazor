window.temporalOps = window.temporalOps || {};

window.temporalOps.captureViewport = function () {
    const shells = Array.from(document.querySelectorAll('.table-shell'));
    return {
        x: window.scrollX || 0,
        y: window.scrollY || 0,
        shells: shells.map((element, index) => ({
            index,
            left: element.scrollLeft || 0,
            top: element.scrollTop || 0
        }))
    };
};

window.temporalOps.restoreViewport = function (state) {
    if (!state) return;

    const restore = () => {
        try {
            if (Array.isArray(state.shells)) {
                const shells = Array.from(document.querySelectorAll('.table-shell'));
                for (const saved of state.shells) {
                    const element = shells[saved.index];
                    if (element) {
                        element.scrollLeft = saved.left || 0;
                        element.scrollTop = saved.top || 0;
                    }
                }
            }

            window.scrollTo(state.x || 0, state.y || 0);
        } catch {
            // Best-effort viewport restoration for live monitoring refreshes.
        }
    };

    requestAnimationFrame(() => requestAnimationFrame(restore));
};

window.temporalOps.loadPinnedWorkflows = function () {
    try {
        const raw = localStorage.getItem('temporalops:pinned-workflows');
        const parsed = raw ? JSON.parse(raw) : [];
        return Array.isArray(parsed) ? parsed.filter(x => typeof x === 'string' && x.length > 0) : [];
    } catch {
        return [];
    }
};

window.temporalOps.savePinnedWorkflows = function (workflowIds) {
    try {
        const unique = Array.from(new Set((workflowIds || []).filter(x => typeof x === 'string' && x.length > 0)));
        localStorage.setItem('temporalops:pinned-workflows', JSON.stringify(unique));
    } catch {
        // Local storage may be unavailable in private mode. Pinning still works in memory.
    }
};
