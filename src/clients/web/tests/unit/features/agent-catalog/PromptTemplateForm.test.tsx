import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { PromptTemplateForm, placeholdersIn } from '../../../../src/features/agent-catalog/artifacts/PromptTemplateForm';
import '../../../../src/i18n';
import type { CatalogViolation, PromptTemplateDocument } from '../../../../src/platform/models/agentCatalog';

const empty: PromptTemplateDocument = { group: 'specialist', responseContract: 'agent-response/1', text: '', slots: [] };

function Harness({ initial = empty, violations = [] }: { initial?: PromptTemplateDocument; violations?: CatalogViolation[] }) {
  const [value, setValue] = useState(initial);
  return (
    <>
      <PromptTemplateForm value={value} onChange={setValue} violations={violations} />
      <output data-testid="value">{JSON.stringify(value)}</output>
    </>
  );
}

const current = () => JSON.parse(screen.getByTestId('value').textContent ?? '{}') as PromptTemplateDocument;

describe('PromptTemplateForm', () => {
  it('finds each placeholder once', () => {
    expect(placeholdersIn('{{agent_id}} and {{ display_name }} and {{agent_id}}')).toEqual(['agent_id', 'display_name']);
  });

  it('offers the slots as choices and shows the remaining length', () => {
    render(<Harness initial={{ ...empty, text: 'abc' }} />);
    for (const label of ['Agent identifier', 'Display name', 'Philosophy profile', 'Tool policy', 'Evidence context']) {
      expect(screen.getByRole('checkbox', { name: label })).toBeInTheDocument();
    }
    expect(screen.getByText(/1 to 32,000 characters/)).toBeInTheDocument();
    expect(screen.getByText(/31997 characters remaining/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Tool policy' }));
    expect(current().slots).toEqual(['tool_policy']);
  });

  it('compares the placeholders with the declared slots without blocking', () => {
    render(<Harness initial={{ ...empty, text: 'Use {{agent_id}}', slots: ['display_name'] }} />);
    expect(screen.getByText('Slot display_name is declared but its placeholder is missing from the text')).toBeInTheDocument();
    expect(screen.getByText('Slot agent_id is present in the text but not declared')).toBeInTheDocument();
  });

  it('edits the text, the group, and the optional major version', () => {
    render(<Harness />);
    fireEvent.change(screen.getByLabelText(/Prompt text/), { target: { value: 'Hello' } });
    fireEvent.change(screen.getByLabelText('Dimension set major version'), { target: { value: '2' } });
    expect(current().text).toBe('Hello');
    expect(current().dimensionSetMajor).toBe(2);
    fireEvent.change(screen.getByLabelText('Dimension set major version'), { target: { value: '' } });
    expect(current().dimensionSetMajor).toBeUndefined();
  });

  it('shows violations at their locations', () => {
    render(
      <Harness
        initial={{ ...empty, text: 'x', slots: ['agent_id'] }}
        violations={[
          { code: 'schema-violation', pointer: '/text', detail: 'too long', location: 'field:text' },
          { code: 'unknown-tool', pointer: '/slots/0', detail: 'slot', location: 'entry:slots:0' },
        ]}
      />,
    );
    expect(screen.getByText('The record does not match its format: too long')).toBeInTheDocument();
    expect(screen.getByText('The tool slot is not on the tool allowlist.')).toBeInTheDocument();
  });
});
