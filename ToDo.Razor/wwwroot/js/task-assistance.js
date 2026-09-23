(function () {
    'use strict';

    function initialize() {
        const root = document.getElementById('task-assistance');
        if (!root || root.dataset.assistInitialized === 'true') return;
        root.dataset.assistInitialized = 'true';

        const find = (name) => root.querySelector('[data-assist-' + name + ']');
        const form = find('form');
        const buttons = Array.from(form.querySelectorAll('button[name="purpose"]'));
        const requestId = find('request-id');
        const submitPurpose = find('submit-purpose');
        const statusLabel = find('status-label');
        const updated = find('updated');
        const workingNote = find('working-note');
        const unavailable = find('unavailable');
        const error = find('error');
        const connection = find('connection');
        const connectionMessage = find('connection-message');
        const refreshButton = find('refresh');
        const resultPanel = find('result');
        const shortResult = find('short-result');
        const preview = find('preview');
        const longResult = find('long-result');
        const details = find('result-details');
        const copyButton = find('copy');
        const copyFeedback = find('copy-feedback');
        const footer = find('footer');
        const sessionLink = find('session-link');
        const names = ['Pending', 'Running', 'WaitingApproval', 'Retrying', 'Completed', 'Failed', 'Cancelled'];
        let currentResult = root.dataset.status === 'Completed'
            ? (shortResult.textContent || longResult.textContent) : '';
        let active = root.dataset.hasActive === 'true';
        let canRequest = root.dataset.canRequest !== 'false';
        let submitting = false;
        let accessLost = false;
        let timer = null;
        let controller = null;

        function setActive(value) {
            active = value;
            root.dataset.hasActive = value ? 'true' : 'false';
            workingNote.hidden = !value;
            buttons.forEach((button) => { button.disabled = value || submitting || accessLost || !canRequest; });
            unavailable.hidden = canRequest || accessLost;
        }

        function displayResult(text) {
            const changed = text !== currentResult;
            currentResult = text;
            const isLong = text.length > 1200;
            resultPanel.hidden = !text.trim();
            shortResult.hidden = isLong;
            shortResult.textContent = isLong ? '' : text;
            preview.hidden = !isLong;
            preview.textContent = isLong ? Array.from(text).slice(0, 280).join('') + '…' : '';
            details.hidden = !isLong;
            longResult.textContent = isLong ? text : '';
            if (changed) {
                details.open = false;
                copyFeedback.textContent = '';
            }
        }

        function stopTimer() {
            if (timer !== null) window.clearTimeout(timer);
            timer = null;
        }

        function schedule() {
            stopTimer();
            if (active && !document.hidden && !submitting && !accessLost) {
                timer = window.setTimeout(() => poll(), 8000);
            }
        }

        function clearPrivateData() {
            accessLost = true;
            stopTimer();
            displayResult('');
            error.textContent = '';
            error.hidden = true;
            updated.textContent = '';
            updated.hidden = true;
            sessionLink.removeAttribute('href');
            footer.hidden = true;
            requestId.value = '';
            root.dataset.jobId = '';
            root.dataset.status = '';
            statusLabel.textContent = '暂时无法查看这份建议';
            setActive(false);
            connectionMessage.textContent = '登录状态或任务权限已变化。为保护你的信息，建议已隐藏，请刷新页面后重试。';
            refreshButton.hidden = true;
            connection.hidden = false;
        }

        function applyStatus(data) {
            if (!data || typeof data.hasResult !== 'boolean' || typeof data.isActive !== 'boolean') {
                throw new Error('Invalid assistance status');
            }
            const status = typeof data.status === 'number' ? names[data.status] : data.status;
            if (data.hasResult && !names.includes(status)) throw new Error('Unknown assistance status');
            if (typeof data.requestId === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(data.requestId)) {
                requestId.value = data.requestId;
            }
            const previousJob = root.dataset.jobId;
            if (typeof data.canRequest === 'boolean') {
                canRequest = data.canRequest;
                root.dataset.canRequest = canRequest ? 'true' : 'false';
            }
            root.dataset.jobId = data.hasResult ? String(data.jobId) : '';
            root.dataset.status = data.hasResult ? status : '';
            if (previousJob !== root.dataset.jobId) details.open = false;
            statusLabel.textContent = data.hasResult && typeof data.statusLabel === 'string'
                ? data.statusLabel : '选择一种帮助，随时开始';
            updated.textContent = data.hasResult && typeof data.updatedAtLabel === 'string' && data.updatedAtLabel
                ? '更新于 ' + data.updatedAtLabel : '';
            updated.hidden = !updated.textContent;
            error.textContent = data.hasResult && typeof data.error === 'string' ? data.error : '';
            error.hidden = !error.textContent;
            displayResult(data.hasResult && status === 'Completed' && typeof data.result === 'string' ? data.result : '');
            footer.hidden = true;
            sessionLink.removeAttribute('href');
            if (data.hasResult && typeof data.sessionUrl === 'string' && data.sessionUrl) {
                const link = new URL(data.sessionUrl, window.location.href);
                if (link.origin === window.location.origin && /\/AiSessions\/Details(?:\/|$)/i.test(link.pathname)) {
                    sessionLink.href = link.href;
                    footer.hidden = false;
                }
            }
            connection.hidden = true;
            setActive(data.hasResult && data.isActive);
        }

        async function poll(force) {
            if (controller || document.hidden || submitting || accessLost || (!active && !force)) return;
            stopTimer();
            const currentController = new AbortController();
            controller = currentController;
            const timeout = window.setTimeout(() => currentController.abort(), 12000);
            refreshButton.disabled = true;
            try {
                const response = await window.fetch(root.dataset.statusUrl, {
                    method: 'GET', credentials: 'same-origin', cache: 'no-store',
                    headers: { 'Accept': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
                    signal: currentController.signal
                });
                if (submitting) return;
                if ([401, 403, 404].includes(response.status) || response.redirected) {
                    clearPrivateData();
                    return;
                }
                if (!response.ok) throw new Error('Assistance status unavailable');
                const data = await response.json();
                if (!submitting && !accessLost) applyStatus(data);
            } catch (_) {
                if (!document.hidden && !submitting && !accessLost) {
                    connectionMessage.textContent = '暂时无法更新状态，当前内容尚未刷新。可重新检查，或稍后刷新页面；请不要重复提交。';
                    connection.hidden = false;
                    refreshButton.hidden = false;
                }
            } finally {
                window.clearTimeout(timeout);
                controller = null;
                refreshButton.disabled = false;
                schedule();
            }
        }

        form.addEventListener('submit', (event) => {
            if (submitting || active || accessLost || !canRequest) {
                event.preventDefault();
                return;
            }
            const selected = event.submitter && event.submitter.name === 'purpose'
                ? event.submitter : buttons.find((button) => !button.disabled);
            if (!selected) {
                event.preventDefault();
                return;
            }
            // Disabled submit buttons are omitted from POST, so preserve the chosen action first.
            submitPurpose.value = selected.value;
            submitPurpose.disabled = false;
            submitting = true;
            stopTimer();
            if (controller) controller.abort();
            buttons.forEach((button) => { button.disabled = true; });
            statusLabel.textContent = '正在提交，请稍候…';
        });

        copyButton.addEventListener('click', async () => {
            if (!currentResult || accessLost) return;
            copyButton.disabled = true;
            try {
                if (!navigator.clipboard || !window.isSecureContext) throw new Error('Clipboard unavailable');
                await navigator.clipboard.writeText(currentResult);
                copyFeedback.textContent = '已复制，可自行整理后使用。';
            } catch (_) {
                if (currentResult.length > 1200) details.open = true;
                copyFeedback.textContent = '浏览器未允许复制，请展开或选中建议文字后手动复制。';
            } finally {
                copyButton.disabled = false;
            }
        });

        refreshButton.addEventListener('click', () => poll(true));
        document.addEventListener('visibilitychange', () => {
            stopTimer();
            if (document.hidden) {
                if (controller) controller.abort();
            } else if (active) {
                poll();
            }
        });
        window.addEventListener('pagehide', () => {
            stopTimer();
            if (controller) controller.abort();
        });
        window.addEventListener('pageshow', (event) => {
            if (!event.persisted) return;
            submitting = false;
            submitPurpose.disabled = true;
            setActive(active);
            poll(true);
        });
        schedule();
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
    else initialize();
})();
