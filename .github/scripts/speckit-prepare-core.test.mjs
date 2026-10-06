import assert from 'node:assert/strict';
import test from 'node:test';

import {
  LABELS,
  PENDING_LABEL,
  TWIN_LABEL,
  buildPrompt,
  buildSpecEntry,
  countOpenChecklistItems,
  deriveStage,
  findCycle,
  implementBlockers,
  parseInferenceOutput,
  parseSpec,
  parseTwinFolder,
  planImplementRequest,
  planSync,
  renderTwinBody,
  stageLabelChange,
  validateLinks,
} from './speckit-prepare-core.mjs';

const context = { serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' };

const SPEC = `# Feature Specification: Platform Persistence Foundation

**Feature Branch**: \`20261005-130700-platform-persistence\`

**Input**: User description: "Establish the shared persistence foundation."

## User Scenarios & Testing *(mandatory)*

- Not an assumption that depends on anything.

## Assumptions

- **Dependencies**: builds on the existing host scaffold.
- Production hosting values are deferred.
- **Relationship to 005**: sibling foundation.
`;

function specEntry(folder, title = `Title ${folder}`, artifacts = ['spec.md']) {
  return [folder, { spec: { folder, title, summary: `Summary ${folder}`, dependencyNotes: [] }, artifacts }];
}

function twin(number, folder, { state = 'open', stateReason = null, labels = [TWIN_LABEL], title, body } = {}) {
  const spec = { folder, title: title ?? `Title ${folder}`, summary: `Summary ${folder}`, dependencyNotes: [] };
  return {
    number,
    id: number * 1000,
    state,
    state_reason: stateReason,
    title: spec.title,
    body: body ?? renderTwinBody(spec, ['spec.md'], context),
    labels: labels.map((name) => ({ name })),
  };
}

test('parses title, summary, and dependency notes from a spec', () => {
  const spec = parseSpec('20261005-130700-platform-persistence', SPEC);
  assert.equal(spec.title, 'Platform Persistence Foundation');
  assert.equal(spec.summary, 'Establish the shared persistence foundation.');
  assert.deepEqual(spec.dependencyNotes, [
    '**Dependencies**: builds on the existing host scaffold.',
    '**Relationship to 005**: sibling foundation.',
  ]);
});

test('falls back to the folder name when a spec has no heading', () => {
  assert.equal(parseSpec('20261005-000000-x', 'no heading').title, '20261005-000000-x');
});

test('renders a clickable spec line that round-trips to the folder', () => {
  const body = renderTwinBody({ folder: 'abc-1', title: 'T', summary: 'S', dependencyNotes: [] }, ['spec.md', 'tasks.md'], context);
  assert.match(body, /^\*\*Spec\*\*: \[`specs\/abc-1`\]\(https:\/\/github\.com\/octo\/repo\/tree\/main\/specs\/abc-1\)$/m);
  assert.match(body, /- \[tasks\.md\]\(https:\/\/github\.com\/octo\/repo\/blob\/main\/specs\/abc-1\/tasks\.md\)/);
  assert.doesNotMatch(body, /plan\.md/);
  assert.equal(parseTwinFolder(body), 'abc-1');
  assert.equal(parseTwinFolder(body.replaceAll('\n', '\r\n')), 'abc-1');
});

test('treats missing, duplicated, or unsafe spec lines as unreadable', () => {
  const line = '**Spec**: [`specs/abc-1`](https://github.com/octo/repo/tree/main/specs/abc-1)';
  assert.equal(parseTwinFolder('no identity'), null);
  assert.equal(parseTwinFolder(`${line}\n${line}`), null);
  assert.equal(parseTwinFolder('**Spec**: [`specs/..`](https://x)'), null);
  assert.equal(parseTwinFolder('**Spec**: specs/abc-1'), null);
  assert.equal(parseTwinFolder(undefined), null);
});

test('plans creates for new specs and leaves matching twins unchanged', () => {
  const specs = new Map([specEntry('a'), specEntry('b')]);
  const plan = planSync({ specs, issues: [twin(1, 'a')], context });
  assert.deepEqual(plan.create.map((item) => item.folder), ['b']);
  assert.equal(plan.update.length, 0);
  assert.equal(plan.close.length, 0);
});

test('plans updates when the title or body changed', () => {
  const specs = new Map([specEntry('a', 'New title')]);
  const plan = planSync({ specs, issues: [twin(1, 'a', { title: 'Old title' })], context });
  assert.deepEqual(plan.update.map((item) => item.issue.number), [1]);
  assert.equal(plan.update[0].title, 'New title');
});

test('repairs a twin body that was edited without breaking its spec line', () => {
  const specs = new Map([specEntry('a')]);
  const edited = twin(1, 'a');
  edited.body = `${edited.body}\nmanual note`;
  assert.equal(planSync({ specs, issues: [edited], context }).update.length, 1);
});

test('reopens twins closed as not planned and leaves completed twins alone', () => {
  const specs = new Map([specEntry('a'), specEntry('b')]);
  const plan = planSync({
    specs,
    issues: [twin(1, 'a', { state: 'closed', stateReason: 'not_planned' }), twin(2, 'b', { state: 'closed', stateReason: 'completed' })],
    context,
  });
  assert.deepEqual(plan.reopen.map((item) => item.folder), ['a']);
  assert.equal(plan.create.length, 0);
  assert.equal(plan.update.length, 0);
});

test('closes open twins whose folder disappeared', () => {
  const plan = planSync({ specs: new Map(), issues: [twin(1, 'gone'), twin(2, 'done', { state: 'closed', stateReason: 'completed' })], context });
  assert.deepEqual(plan.close.map((item) => item.issue.number), [1]);
});

test('keeps the oldest twin and reports duplicates', () => {
  const plan = planSync({ specs: new Map([specEntry('a')]), issues: [twin(5, 'a'), twin(3, 'a')], context });
  assert.equal(plan.duplicates.length, 1);
  assert.equal(plan.duplicates[0].issue.number, 5);
  assert.equal(plan.duplicates[0].primary.number, 3);
  assert.equal(plan.create.length, 0);
});

test('fail-safe: unreadable twins block creates but not updates or closes', () => {
  const specs = new Map([specEntry('a', 'Changed'), specEntry('new')]);
  const plan = planSync({
    specs,
    issues: [twin(1, 'a'), twin(2, 'gone'), { ...twin(3, 'x'), body: 'damaged' }],
    context,
  });
  assert.deepEqual(plan.unreadable.map((issue) => issue.number), [3]);
  assert.equal(plan.create.length, 0);
  assert.deepEqual(plan.skippedCreates.map((item) => item.folder), ['new']);
  assert.equal(plan.update.length, 1);
  assert.equal(plan.close.length, 1);
});

test('builds a prompt that lists new features first and respects the size budget', () => {
  const entries = [
    { folder: 'old', title: 'Old', summary: 'old summary', dependencyNotes: ['depends on nothing'], pending: false },
    { folder: 'new', title: 'New', summary: 'new summary', dependencyNotes: ['builds on old'], pending: true },
  ];
  const prompt = buildPrompt(entries);
  assert.ok(prompt.indexOf('### new (NEW)') < prompt.indexOf('### old'));
  assert.match(prompt, /builds on old/);
  assert.match(prompt, /\{"links":\[\]\}/);

  const small = buildPrompt([...entries, { folder: 'big', title: 'B', summary: 'x'.repeat(5000), dependencyNotes: [], pending: false }], 2600);
  assert.match(small, /existing features omitted/);
  assert.throws(() => buildPrompt([{ ...entries[1], summary: 'x'.repeat(5000) }], 1000), /Prompt budget exceeded/);
});

test('parses inference output, tolerating surrounding text', () => {
  const links = parseInferenceOutput('```json\n{"links":[{"blocked":"b","blockedBy":"a","reason":" because "}]}\n```');
  assert.deepEqual(links, [{ blocked: 'b', blockedBy: 'a', reason: 'because' }]);
  assert.deepEqual(parseInferenceOutput('{"links":[]}'), []);
});

test('rejects malformed inference output', () => {
  assert.throws(() => parseInferenceOutput(''), /empty/);
  assert.throws(() => parseInferenceOutput('nothing here'), /no JSON/);
  assert.throws(() => parseInferenceOutput('{"links":'), /no JSON|not valid JSON/);
  assert.throws(() => parseInferenceOutput('{"edges":[]}'), /"links" array/);
  assert.throws(() => parseInferenceOutput('{"links":[{"blocked":"a","blockedBy":"b"}]}'), /no reason/);
  assert.throws(() => parseInferenceOutput('{"links":[{"blocked":1,"blockedBy":"b","reason":"r"}]}'), /string/);
});

test('detects cycles', () => {
  assert.equal(findCycle([['a', 'b'], ['b', 'c']]), null);
  assert.deepEqual(findCycle([['a', 'b'], ['b', 'c'], ['c', 'a']]), ['a', 'b', 'c', 'a']);
});

test('validates links against open twins, pending twins, duplicates, and cycles', () => {
  const base = { openFolders: new Set(['a', 'b', 'c']), pendingFolders: new Set(['c']), existingEdges: [['b', 'a']] };
  const link = (blocked, blockedBy) => ({ blocked, blockedBy, reason: 'r' });

  const result = validateLinks({ ...base, links: [link('c', 'b'), link('c', 'b'), link('c', 'a')] });
  assert.deepEqual(result.accepted.map((l) => `${l.blocked}<${l.blockedBy}`), ['c<b', 'c<a']);
  assert.equal(result.skipped.length, 1);

  assert.throws(() => validateLinks({ ...base, links: [link('c', 'zzz')] }), /unknown or closed/);
  assert.throws(() => validateLinks({ ...base, links: [link('c', 'c')] }), /itself/);
  assert.throws(() => validateLinks({ ...base, links: [link('a', 'b')] }), /no newly added/);
  assert.throws(() => validateLinks({ ...base, links: [link('c', 'b'), link('a', 'c')] }), /cycle/);
  assert.equal(validateLinks({ ...base, links: [link('b', 'a')], pendingFolders: new Set(['a']) }).skipped.length, 1);
});

test('exports the twin and pending label names', () => {
  assert.equal(TWIN_LABEL, 'speckit:spec');
  assert.equal(PENDING_LABEL, 'speckit:deps-pending');
  assert.deepEqual(LABELS.map((label) => label.name).filter((name) => name.startsWith('speckit:stage:')), [
    'speckit:stage:specified',
    'speckit:stage:planned',
    'speckit:stage:tasked',
    'speckit:stage:implement',
    'speckit:stage:implementing',
    'speckit:stage:implemented',
    'speckit:stage:discarded',
  ]);
});

test('counts open checklist items across files', () => {
  assert.equal(countOpenChecklistItems([]), 0);
  assert.equal(countOpenChecklistItems(['- [x] a\n- [ ] b\r\n', '  * [ ] c\n- [X] d\n- [ ]no space ok\n']), 3);
});

test('lists implement blockers for the stage and open checklist items', () => {
  assert.deepEqual(implementBlockers({ folder: 'f', computedStage: 'tasked', openChecklistItems: 0 }), []);
  const reasons = implementBlockers({ folder: 'f', computedStage: 'planned', openChecklistItems: 2 });
  assert.equal(reasons.length, 2);
  assert.match(reasons[0], /stage `planned`.*`tasked`/);
  assert.match(reasons[1], /2 checklist item\(s\) in `specs\/f\/checklists\/`/);
});

test('builds spec entries from raw file contents', () => {
  const entry = buildSpecEntry('f', { specMarkdown: SPEC, hasPlan: true, tasksMarkdown: '- [ ] T001 x\n', checklistMarkdowns: ['- [ ] a\n'] });
  assert.deepEqual(entry.artifacts, ['spec.md', 'plan.md', 'tasks.md']);
  assert.equal(entry.openChecklistItems, 1);
  assert.equal(entry.spec.title, 'Platform Persistence Foundation');
  assert.deepEqual(buildSpecEntry('f', { specMarkdown: SPEC, hasPlan: false, tasksMarkdown: null, checklistMarkdowns: [] }).artifacts, ['spec.md']);
});

test('plans implementation requests', () => {
  const tasked = { artifacts: ['spec.md', 'plan.md', 'tasks.md'], tasks: '- [ ] T001 x\n', openChecklistItems: 0 };
  const flagged = (issue) => ({ ...issue, labels: [...issue.labels, { name: 'speckit:stage:tasked' }, { name: 'speckit:stage:implement' }] });

  const accepted = planImplementRequest({ issue: flagged(twin(1, 'a')), entry: tasked, requester: { login: 'dev', canWrite: true } });
  assert.equal(accepted.action, 'accept');
  assert.deepEqual(accepted.labels, { add: [], remove: ['speckit:stage:tasked'] });

  const closed = planImplementRequest({ issue: flagged(twin(1, 'a', { state: 'closed' })), entry: tasked, requester: { login: 'dev', canWrite: true } });
  assert.deepEqual([closed.action, closed.labels], ['reject', { add: [], remove: ['speckit:stage:implement'] }]);

  const missing = planImplementRequest({ issue: flagged(twin(1, 'a')), entry: null, requester: { login: 'dev', canWrite: true } });
  assert.match(missing.reasons[0], /does not exist on the default branch/);

  const both = planImplementRequest({ issue: flagged(twin(1, 'a')), entry: { ...tasked, openChecklistItems: 1 }, requester: { login: 'dev', canWrite: false } });
  assert.equal(both.action, 'reject');
  assert.equal(both.stage, 'tasked');
  assert.deepEqual(both.labels, { add: [], remove: ['speckit:stage:implement'] });
  assert.equal(both.reasons.length, 2);
});

test('sync planning keeps a valid implement flag and revokes an invalid one', () => {
  const tasks = '- [ ] T001 x\n';
  const entry = (overrides) => ({ ...specEntry('a')[1], artifacts: ['spec.md', 'plan.md', 'tasks.md'], tasks, openChecklistItems: 0, ...overrides });
  const flagged = { ...twin(1, 'a'), labels: [{ name: TWIN_LABEL }, { name: 'speckit:stage:implement' }] };

  const keep = planSync({ specs: new Map([['a', entry()]]), issues: [structuredClone(flagged)], context });
  assert.equal(keep.relabel.length, 0);
  assert.deepEqual(keep.update.map((item) => [item.stage, item.labels, item.revoked]), [['implement', { add: [], remove: [] }, null]]);

  const revoke = planSync({ specs: new Map([['a', entry({ openChecklistItems: 3 })]]), issues: [structuredClone(flagged)], context });
  const [item] = [...revoke.update, ...revoke.relabel];
  assert.equal(item.stage, 'tasked');
  assert.deepEqual(item.labels, { add: ['speckit:stage:tasked'], remove: ['speckit:stage:implement'] });
  assert.match(item.revoked[0], /3 checklist item/);
});

test('derives the stage from artifacts and task checkboxes', () => {
  assert.equal(deriveStage(['spec.md'], null), 'specified');
  assert.equal(deriveStage(['spec.md', 'plan.md'], null), 'planned');
  assert.equal(deriveStage(['spec.md', 'plan.md', 'tasks.md'], '# Tasks\n\nNo tasks yet.\n'), 'tasked');
  assert.equal(deriveStage(['spec.md', 'plan.md', 'tasks.md'], '- [ ] T001 [P] [US1] one\n- [ ] T002 two\n'), 'tasked');
  assert.equal(deriveStage(['spec.md', 'tasks.md'], '- [x] T001 one\r\n- [ ] T002 two\r\n'), 'implementing');
  assert.equal(deriveStage(['spec.md', 'plan.md', 'tasks.md'], '- [x] T001 one\n  * [X] T1000 two\n- [ ] not a task\n'), 'implemented');
  assert.equal(deriveStage(['spec.md', 'tasks.md'], '- [x] ST001 not a task id\n- [ ] T002 two\n'), 'tasked');
});

test('computes stage label changes without touching other labels', () => {
  const issue = { labels: [{ name: TWIN_LABEL }, { name: 'speckit:stage:planned' }, { name: 'speckit:stage:tasked' }, { name: 'human' }] };
  assert.deepEqual(stageLabelChange(issue, 'tasked'), { add: [], remove: ['speckit:stage:planned'] });
  assert.deepEqual(stageLabelChange({ labels: [{ name: 'human' }] }, 'specified'), { add: ['speckit:stage:specified'], remove: [] });
});

test('plans stage labels for creates, relabels, reopens, and closes', () => {
  const withStage = (issue, stage) => ({ ...issue, labels: [...issue.labels, { name: `speckit:stage:${stage}` }] });
  const specs = new Map([
    specEntry('new'),
    ['a', { ...specEntry('a')[1], artifacts: ['spec.md', 'plan.md'] }],
    specEntry('back'),
    specEntry('same'),
  ]);
  const plan = planSync({
    specs,
    issues: [
      withStage(twin(1, 'a'), 'specified'),
      withStage(twin(2, 'back', { state: 'closed', stateReason: 'not_planned' }), 'discarded'),
      withStage(twin(3, 'gone'), 'tasked'),
      withStage(twin(4, 'same'), 'specified'),
      twin(5, 'old-gone', { state: 'closed', stateReason: 'not_planned' }),
      withStage(twin(6, 'done-gone', { state: 'closed', stateReason: 'completed' }), 'implemented'),
    ],
    context,
  });
  assert.deepEqual(plan.create.map((item) => [item.folder, item.stage]), [['new', 'specified']]);
  assert.deepEqual(plan.reopen[0].labels, { add: ['speckit:stage:specified'], remove: ['speckit:stage:discarded'] });
  assert.deepEqual(plan.close[0].labels, { add: ['speckit:stage:discarded'], remove: ['speckit:stage:tasked'] });
  assert.deepEqual(plan.relabel.map((item) => [item.issue.number, item.stage]), [[5, 'discarded']]);
  assert.deepEqual(plan.update.map((item) => [item.issue.number, item.stage, item.labels]), [
    [1, 'planned', { add: ['speckit:stage:planned'], remove: ['speckit:stage:specified'] }],
  ]);
});
