import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { ModelSelectionForm } from '../../../../src/features/agent-catalog/artifacts/ModelSelectionForm';
import type { CatalogViolation, ModelSelectionDocument } from '../../../../src/platform/models/agentCatalog';
import '../../../../src/i18n';

const EMPTY = { provider: 'simulated', model: '', deployment: '', parameters: {} } as ModelSelectionDocument;

function Harness({
  initial = EMPTY,
  violations,
  spy,
}: {
  initial?: ModelSelectionDocument;
  violations?: CatalogViolation[];
  spy?: (value: ModelSelectionDocument) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <ModelSelectionForm
      value={value}
      violations={violations}
      onChange={(next) => {
        spy?.(next);
        setValue(next);
      }}
    />
  );
}

describe('ModelSelectionForm', () => {
  it('offers the fields of a model selection and none for credentials or endpoints', () => {
    render(<Harness />);
    for (const name of [/Provider/, /^Model/, /Deployment or configuration name/, /Temperature/, /Top P/, /Maximum output tokens/, /Reasoning effort/]) {
      expect(screen.getByLabelText(name)).toBeInTheDocument();
    }
    expect(screen.queryByLabelText(/key|secret|token(?!s)|endpoint|url|password/i)).toBeNull();
    expect(screen.getAllByText(/Letters, digits, and/)).toHaveLength(2);
  });

  it('reports the model and deployment as typed', async () => {
    const spy = vi.fn();
    render(<Harness spy={spy} />);
    await userEvent.type(screen.getByLabelText(/^Model/), 'a');
    expect(spy).toHaveBeenLastCalledWith({ ...EMPTY, model: 'a' });
    await userEvent.type(screen.getByLabelText(/Deployment or configuration name/), 'b');
    expect(spy).toHaveBeenLastCalledWith({ ...EMPTY, model: 'a', deployment: 'b' });
  });

  it('states the parameter ranges and removes a cleared parameter', async () => {
    const spy = vi.fn();
    render(<Harness initial={{ ...EMPTY, parameters: { temperature: 1 } }} spy={spy} />);
    expect(screen.getByText(/From 0 to 2/)).toBeInTheDocument();
    expect(screen.getByText(/From 0 to 1/)).toBeInTheDocument();
    expect(screen.getByText(/Whole numbers from 1 to 32,768/)).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText(/Temperature/));
    expect(spy).toHaveBeenLastCalledWith({ ...EMPTY, parameters: {} });
    await userEvent.type(screen.getByLabelText(/Maximum output tokens/), '5');
    expect(spy).toHaveBeenLastCalledWith({ ...EMPTY, parameters: { maxOutputTokens: 5 } });
  });

  it('does not block values outside the ranges or the name pattern', async () => {
    const spy = vi.fn();
    render(<Harness spy={spy} />);
    await userEvent.type(screen.getByLabelText(/Top P/), '7');
    expect(spy).toHaveBeenLastCalledWith({ ...EMPTY, parameters: { topP: 7 } });
    await userEvent.type(screen.getByLabelText(/^Model/), 'a b');
    expect(spy).toHaveBeenLastCalledWith({ ...EMPTY, model: 'a b', parameters: { topP: 7 } });
  });

  it('chooses the provider and the reasoning effort', async () => {
    const spy = vi.fn();
    render(<Harness spy={spy} />);
    await userEvent.click(screen.getByRole('combobox', { name: /Provider/ }));
    await userEvent.click(screen.getByRole('option', { name: 'OpenAI' }));
    expect(spy).toHaveBeenLastCalledWith({ ...EMPTY, provider: 'openai' });
    await userEvent.click(screen.getByRole('combobox', { name: /Reasoning effort/ }));
    await userEvent.click(screen.getByRole('option', { name: 'High' }));
    expect(spy).toHaveBeenLastCalledWith({ ...EMPTY, provider: 'openai', parameters: { reasoningEffort: 'high' } });
  });

  it('shows violations at their fields only', () => {
    const violations: CatalogViolation[] = [
      { code: 'provider-not-permitted', pointer: '/provider', detail: '', location: 'field:provider' },
      { code: 'schema-violation', pointer: '/model', detail: 'bad model', location: 'field:model' },
      { code: 'schema-violation', pointer: '/other', detail: 'elsewhere', location: 'field:other' },
    ];
    render(<Harness violations={violations} />);
    expect(screen.getByText('This provider is not permitted in this environment.')).toBeInTheDocument();
    expect(screen.getByText(/bad model/)).toBeInTheDocument();
    expect(screen.queryByText(/elsewhere/)).toBeNull();
  });
});
