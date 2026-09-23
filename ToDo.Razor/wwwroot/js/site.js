(function () {
    function todayInBeijing() {
        const parts = new Intl.DateTimeFormat('en-US', {
            timeZone: 'Asia/Shanghai',
            year: 'numeric',
            month: '2-digit',
            day: '2-digit'
        }).formatToParts(new Date());
        const values = Object.fromEntries(parts.map(part => [part.type, part.value]));
        return `${values.year}-${values.month}-${values.day}`;
    }

    function escapeHtml(value) {
        return String(value).replace(/[&<>'"]/g, function (character) {
            return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character];
        });
    }

    function renderStatus(status, steps, current) {
        const percent = Math.max(8, Math.min(94, Math.round(((current + 0.45) / steps.length) * 100)));
        status.innerHTML = `
            <div class="ai-generation-status-head">
                <span><span class="ai-spinner" aria-hidden="true"></span><strong>AI 正在处理</strong></span>
                <strong>${percent}%</strong>
            </div>
            <div class="ai-progress-track"><i style="width:${percent}%"></i></div>
            <div class="ai-progress-steps">
                ${steps.map((step, index) => `
                    <div class="ai-progress-step ${index < current ? 'done' : ''} ${index === current ? 'active' : ''}">
                        <span>${index < current ? '✓' : index + 1}</span>
                        <em>${escapeHtml(step)}</em>
                    </div>`).join('')}
            </div>
            <small class="ai-generation-note">正在处理，请稍候。按钮已锁定，完成后页面会自动更新。</small>`;
    }

    function startStatus(status, button, steps, busyText) {
        const originalHtml = button.innerHTML;
        let current = 0;
        button.disabled = true;
        button.setAttribute('aria-busy', 'true');
        button.classList.add('ai-busy');
        button.innerHTML = `<span class="ai-spinner" aria-hidden="true"></span>${busyText}`;
        status.classList.remove('d-none');
        renderStatus(status, steps, current);

        const timer = window.setInterval(function () {
            if (current < steps.length - 1) {
                current += 1;
                renderStatus(status, steps, current);
            }
        }, 1200);

        return function restore() {
            window.clearInterval(timer);
            button.disabled = false;
            button.removeAttribute('aria-busy');
            button.classList.remove('ai-busy');
            button.innerHTML = originalHtml;
        };
    }

    function bindForm(formSelector, buttonSelector, statusSelector, steps, busyText) {
        const form = document.querySelector(formSelector);
        const button = document.querySelector(buttonSelector);
        const status = document.querySelector(statusSelector);
        if (!form || !button || !status) return;

        form.addEventListener('submit', function (event) {
            const jqueryInvalid = window.jQuery && window.jQuery.validator && !window.jQuery(form).valid();
            if (event.defaultPrevented || !form.checkValidity() || jqueryInvalid) return;

            if (form.dataset.aiBusy === 'true') {
                event.preventDefault();
                return;
            }

            form.dataset.aiBusy = 'true';
            startStatus(status, button, steps, busyText);
        });
    }

    function bindLink(linkSelector, statusSelector, steps, busyText) {
        const link = document.querySelector(linkSelector);
        const status = document.querySelector(statusSelector);
        if (!link || !status) return;

        link.addEventListener('click', function (event) {
            if (link.dataset.aiBusy === 'true') {
                event.preventDefault();
                return;
            }

            link.dataset.aiBusy = 'true';
            link.setAttribute('aria-disabled', 'true');
            startStatus(status, link, steps, busyText);
        });
    }

    window.ToDoTime = Object.freeze({ todayInBeijing });
    window.AiGeneration = { bindForm, bindLink };
})();
