import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { DimensionSetForm } from '../../../../src/features/agent-catalog/artifacts/DimensionSetForm';
import type { CatalogViolation, DimensionSetDocument } from '../../../../src/platform/models/agentCatalog';
import '../../../../src/i18n';
import { TOOLS } from './fixtures';

function Harness({
  initial = { dimensions: [] },
  violations,
  allowlist = TOOLS,
  spy,
}: {
  initial?: DimensionSetDocument;
  violations?: CatalogViolation[];
  allowlist?: string[];
  spy?: (value: DimensionSetDocument) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <DimensionSetForm
      value={value}
      allowlist={allowlist}
      violations={violations}
      onChange={(next) => {
        spy?.(next);
        setValue(next);
      }}
    />
  );
}

describe('DimensionSetForm', () => {
  it('states the rule and the empty text, and adds and removes dimensions', async () => {
    render(<Harness />);
    expect(screen.getByText(/1 to 32 entries/)).toBeInTheDocument();
    expect(screen.getByText(/No dimensions are defined/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Add dimension' }));
    expect(screen.getByLabelText(/Identifier of dimension 1/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Remove dimension 1' }));
    expect(screen.queryByLabelText(/Identifier of dimension 1/)).toBeNull();
  });

  it('reports the entered id and meaning and the chosen evidence tools from the allowlist', async () => {
    const spy = vi.fn();
    render(<Harness initial={{ dimensions: [{ id: '', meaning: '', acceptedEvidence: [] }] }} spy={spy} />);
    await userEvent.type(screen.getByLabelText(/Identifier of dimension 1/), 'a');
    expect(spy).toHaveBeenLastCalledWith({ dimensions: [{ id: 'a', meaning: '', acceptedEvidence: [] }] });
    await userEvent.type(screen.getByLabelText(/Meaning of dimension 1/), 'm');
    expect(spy).toHaveBeenLastCalledWith({ dimensions: [{ id: 'a', meaning: 'm', acceptedEvidence: [] }] });
    expect(screen.getAllByRole('checkbox')).toHaveLength(3);
    await userEvent.click(screen.getByRole('checkbox', { name: 'run.summary' }));
    expect(spy).toHaveBeenLastCalledWith({ dimensions: [{ id: 'a', meaning: 'm', acceptedEvidence: ['run.summary'] }] });
    await userEvent.click(screen.getByRole('checkbox', { name: 'run.summary' }));
    expect(spy).toHaveBeenLastCalledWith({ dimensions: [{ id: 'a', meaning: 'm', acceptedEvidence: [] }] });
  });

  it('hints at duplicate ids without blocking and keeps unlisted tools visible', () => {
    render(
      <Harness
        initial={{
          dimensions: [
            { id: 'pressing', meaning: 'x', acceptedEvidence: ['legacy.tool'] },
            { id: 'pressing', meaning: 'y', acceptedEvidence: [] },
          ],
        }}
      />,
    );
    expect(screen.getAllByText(/The identifier pressing is used more than once/)).toHaveLength(2);
    expect(screen.getByRole('checkbox', { name: 'legacy.tool' })).toBeChecked();
    expect(screen.getByText('The tool legacy.tool is not on the tool allowlist.')).toBeInTheDocument();
  });

  it('shows violations at their locations', () => {
    const violations: CatalogViolation[] = [
      { code: 'schema-violation', pointer: '/dimensions/0', detail: 'bad entry', location: 'entry:dimensions:0' },
      { code: 'unknown-tool', pointer: '/dimensions/0/acceptedEvidence/0', detail: 'evidence.read', location: 'entry:dimensions.0.acceptedEvidence:0' },
      { code: 'schema-violation', pointer: '/dimensions', detail: 'too few', location: 'field:dimensions' },
      { code: 'schema-violation', pointer: '/other', detail: 'elsewhere', location: 'field:other' },
    ];
    render(<Harness initial={{ dimensions: [{ id: 'pressing', meaning: 'x', acceptedEvidence: ['evidence.read'] }] }} violations={violations} />);
    expect(screen.getByText(/bad entry/)).toBeInTheDocument();
    expect(screen.getByText(/too few/)).toBeInTheDocument();
    expect(screen.getAllByText('The tool evidence.read is not on the tool allowlist.')).toHaveLength(1);
    expect(screen.queryByText(/elsewhere/)).toBeNull();
  });
});
