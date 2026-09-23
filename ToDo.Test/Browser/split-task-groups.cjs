// 离线浏览器回归：运行生产 Razor 中的实际脚本，用可控的延迟/失败响应验证交互。
// node --test ToDo.Test/Browser/split-task-groups.cjs
// 可通过 PLAYWRIGHT_MODULE_PATH / PLAYWRIGHT_EXECUTABLE_PATH 指定本机已有运行时。
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { test, before, after } = require('node:test');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE_PATH || 'playwright');
const root = path.resolve(__dirname, '../..');
const razor = fs.readFileSync(path.join(root, 'ToDo.Razor/Pages/Tasks/SplitTask.cshtml'), 'utf8');
const source = [...razor.matchAll(/<script>([\s\S]*?)<\/script>/g)].at(-1)[1];
let browser;
before(async () => {
  browser = await chromium.launch({headless: true, ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? {executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH} : {})});
});
after(async () => { await browser?.close(); });

async function withPage(run) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => route.abort());
  try {
    await page.setContent(`<!doctype html><html><body>
      <div id="dynamicAlert"><span id="alertMessage"></span></div>
      <form id="saveAllForm"><table><tbody><tr id="task-row-1-0"><td>
        <input class="task-title" value="验收任务" name="ProjectTaskGroups[1].Tasks[0].Title">
        <input class="task-desc" value="检查交付"><input class="deadline-input" value="">
        <input class="task-priority" value="Medium"><input name="ProjectTaskGroups[1].Tasks[0].IsDeleted" value="false">
        <select class="project-select-single" data-task-index="0" data-group-key="1" name="ProjectTaskGroups[1].Tasks[0].ProjectId">
          <option value="1" selected>项目一</option><option value="2">项目二</option><option value="3">项目三</option>
        </select>
        <select class="group-select" name="ProjectTaskGroups[1].Tasks[0].GroupId"><option value="42" selected>发布验收分组</option></select>
      </td></tr></tbody></table><button type="submit">保存所有任务</button></form>
      <div id="editTaskModal">
        <input id="editTaskProjectId"><input id="editTaskIndex"><input id="editTaskTitle">
        <input id="editTaskDescription"><input id="editTaskDeadline"><select id="editTaskPriority"><option>Medium</option></select>
        <select id="editTaskProject"><option value="1">项目一</option><option value="2">项目二</option><option value="3">项目三</option></select>
        <select id="editTaskGroupId"></select><button id="saveTaskEditButton" type="button">确定</button>
      </div>
    </body></html>`);
    await page.addScriptTag({path: path.join(root, 'ToDo.Razor/wwwroot/lib/jquery/dist/jquery.min.js')});
    await page.evaluate(() => {
      window.bootstrap = {Modal: class {show() {} hide() {}}};
      window.AiGeneration = {bindForm() {}};
      window.pendingGroups = [];
      $.getJSON = (url, success) => {
        const deferred = $.Deferred();
        pendingGroups.push({url, success, deferred});
        return deferred.promise();
      };
    });
    await page.addScriptTag({content: source});
    await page.waitForFunction(() => pendingGroups.length === 1);
    await run(page);
    assert.deepEqual(errors, []);
  } finally { await page.close(); }
}

async function finish(page, index = 0, data = [{id: 42, name: '发布验收分组'}], fail = false) {
  await page.evaluate(({index, data, fail}) => {
    const request = pendingGroups[index];
    if (fail) request.deferred.reject({status: 503});
    else { request.success(data); request.deferred.resolve(data); }
  }, {index, data, fail});
}

test('initial loading blocks save and keeps the matched group until successful refresh', () => withPage(async page => {
  assert.equal(await page.locator('#saveAllForm button').isDisabled(), true);
  assert.equal(await page.evaluate(() => validateBeforeSave()), false);
  assert.equal(await page.locator('.group-select').inputValue(), '42');
  await finish(page);
  assert.equal(await page.locator('#saveAllForm button').isEnabled(), true);
  assert.equal(await page.evaluate(() => validateBeforeSave()), true);
  assert.equal(await page.evaluate(() => new FormData(document.querySelector('#saveAllForm')).get('ProjectTaskGroups[1].Tasks[0].GroupId')), '42');
  assert.equal(await page.evaluate(() => validateBeforeSave()), false);
}));

test('failed loading preserves selection and offers retry instead of submitting an empty group', () => withPage(async page => {
  await finish(page, 0, null, true);
  assert.equal(await page.locator('.group-select').inputValue(), '42');
  assert.equal(await page.evaluate(() => validateBeforeSave()), false);
  await page.locator('.group-load-retry').click();
  await finish(page, 1);
  assert.equal(await page.locator('.group-load-retry').count(), 0);
  assert.equal(await page.locator('.group-select').inputValue(), '42');
  assert.equal(await page.evaluate(() => validateBeforeSave()), true);
}));

test('out-of-order project responses cannot overwrite the latest project groups', () => withPage(async page => {
  await finish(page);
  await page.selectOption('.project-select-single', '2');
  await page.selectOption('.project-select-single', '3');
  await finish(page, 2, [{id: 300, name: '项目三分组'}]);
  await page.selectOption('.group-select', '300');
  await finish(page, 1, [{id: 200, name: '项目二分组'}]);
  assert.equal(await page.locator('.group-select').inputValue(), '300');
  assert.equal(await page.locator('.group-select').textContent(), '不分组项目三分组');
  assert.equal(await page.evaluate(() => validateBeforeSave()), true);
}));

test('an obsolete failed request cannot disable the latest successful selection', () => withPage(async page => {
  await page.selectOption('.project-select-single', '2');
  await finish(page, 1, [{id: 200, name: '项目二分组'}]);
  await page.selectOption('.group-select', '200');
  await finish(page, 0, null, true);
  assert.equal(await page.locator('.group-load-retry').count(), 0);
  assert.equal(await page.locator('.group-select').inputValue(), '200');
  assert.equal(await page.evaluate(() => validateBeforeSave()), true);
}));

test('editing in the modal and immediately saving waits for both group requests', () => withPage(async page => {
  await finish(page);
  await page.evaluate(() => editTask(1, 0));
  assert.equal(await page.locator('#saveTaskEditButton').isDisabled(), true);
  await page.evaluate(() => saveTaskEdit());
  assert.equal(await page.evaluate(() => pendingGroups.length), 2);
  await finish(page, 1);
  assert.equal(await page.locator('#editTaskGroupId').inputValue(), '42');
  await page.evaluate(() => saveTaskEdit());
  assert.equal(await page.evaluate(() => validateBeforeSave()), false);
  await finish(page, 2);
  assert.equal(await page.locator('.group-select').inputValue(), '42');
  assert.equal(await page.evaluate(() => validateBeforeSave()), true);
}));

test('the modal also supports retry without losing the previous group', () => withPage(async page => {
  await finish(page);
  await page.evaluate(() => editTask(1, 0));
  await finish(page, 1, null, true);
  assert.equal(await page.locator('#saveTaskEditButton').isDisabled(), true);
  await page.locator('#editTaskModal .group-load-retry').click();
  await finish(page, 2);
  assert.equal(await page.locator('#editTaskGroupId').inputValue(), '42');
  assert.equal(await page.locator('#saveTaskEditButton').isEnabled(), true);
}));

test('invalid group payloads block saving and can be retried', () => withPage(async page => {
  await finish(page, 0, {error: 'unavailable'});
  assert.equal(await page.locator('.group-select').inputValue(), '42');
  assert.equal(await page.evaluate(() => validateBeforeSave()), false);
  assert.equal(await page.locator('.group-load-retry').count(), 1);
}));

test('a valid empty group list allows intentionally ungrouped tasks', () => withPage(async page => {
  await finish(page, 0, []);
  assert.equal(await page.locator('.group-select').inputValue(), '');
  assert.equal(await page.evaluate(() => validateBeforeSave()), true);
}));

test('pageshow never enables submit while groups are still loading', () => withPage(async page => {
  await page.evaluate(() => window.dispatchEvent(new Event('pageshow')));
  assert.equal(await page.locator('#saveAllForm button').isDisabled(), true);
  await finish(page);
  assert.equal(await page.evaluate(() => validateBeforeSave()), true);
  await page.evaluate(() => window.dispatchEvent(new Event('pageshow')));
  assert.equal(await page.locator('#saveAllForm button').isEnabled(), true);
}));

test('deleted rows do not block saving other rows with loaded groups', () => withPage(async page => {
  await page.evaluate(() => {
    const row = document.querySelector('#task-row-1-0').cloneNode(true);
    row.id = 'task-row-1-1';
    row.querySelector('.project-select-single').dataset.taskIndex = '1';
    document.querySelector('tbody').append(row);
    loadGroupsByProject($(row).find('.project-select-single'));
  });
  await finish(page, 1);
  await page.evaluate(() => { window.confirm = () => true; deleteTask(1, 0); });
  assert.equal(await page.locator('#saveAllForm button').isEnabled(), true);
  assert.equal(await page.evaluate(() => validateBeforeSave()), true);
}));
