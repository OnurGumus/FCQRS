// Custom formatting for fsdocs tooltips
// Adds line breaks before 'override', 'member' keywords and '|' pipes

(function() {
    function formatTooltipContent() {
        const tips = document.querySelectorAll('.fsdocs-tip');
        tips.forEach(function(tip) {
            // Skip if already processed
            if (tip.dataset.formatted) return;
            tip.dataset.formatted = 'true';

            // Get the HTML content
            let html = tip.innerHTML;

            // Add line break before 'override' (with optional leading spaces)
            html = html.replace(/(\s{2,})(override\s)/g, '<br>$1$2');

            // Add line break before 'member' (with optional leading spaces)
            html = html.replace(/(\s{2,})(member\s)/g, '<br>$1$2');

            // Add line break before 'abstract' (with optional leading spaces)
            html = html.replace(/(\s{2,})(abstract\s)/g, '<br>$1$2');

            // Add line break before 'static' (with optional leading spaces)
            html = html.replace(/(\s{2,})(static\s)/g, '<br>$1$2');

            // Add line break before 'new' for constructors (with optional leading spaces)
            html = html.replace(/(\s{2,})(new\s*:)/g, '<br>$1$2');

            // Add line break before pipe '|' for discriminated unions (with optional leading spaces)
            html = html.replace(/(\s{2,})(\|)/g, '<br>$1$2');

            tip.innerHTML = html;
        });
    }

    // Run on page load
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', formatTooltipContent);
    } else {
        formatTooltipContent();
    }

    // Also run when tooltips become visible (MutationObserver approach)
    const observer = new MutationObserver(function(mutations) {
        mutations.forEach(function(mutation) {
            if (mutation.type === 'attributes' &&
                mutation.attributeName === 'style' &&
                mutation.target.classList.contains('fsdocs-tip')) {
                formatTooltipContent();
            }
        });
    });

    // Observe the document for tooltip visibility changes
    document.addEventListener('DOMContentLoaded', function() {
        const tips = document.querySelectorAll('.fsdocs-tip');
        tips.forEach(function(tip) {
            observer.observe(tip, { attributes: true });
        });
        // Initial format
        formatTooltipContent();
    });
})();
