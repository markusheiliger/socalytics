import assert from 'node:assert/strict';
import test from 'node:test';

import {
  PENDING_LABEL,
  TWIN_LABEL,
  buildPrompt,
  findCycle,
  parseInferenceOutput,
  parseSpec,
  parseTwinFolder,
  planSync,
  renderTwinBody,
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
});
