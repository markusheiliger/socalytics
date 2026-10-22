import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { ToolPolicyForm } from '../../../../src/features/agent-catalog/artifacts/ToolPolicyForm';
import type { CatalogViolation, ToolPolicyDocument } from '../../../../src/platform/models/agentCatalog';
import '../../../../src/i18n';
import { TOOLS, videoAgent } from './fixtures';

function Harness({
  initial = { tools: [] },
  agent,
  violations,
  allowlist = TOOLS,
  spy,
}: {
  initial?: ToolPolicyDocument;
  agent?: typeof videoAgent;
  violations?: CatalogViolation[];
  allowlist?: string[];
  spy?: (value: ToolPolicyDocument) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <ToolPolicyForm
      value={value}
      allowlist={allowlist}
      agent={agent}
      violations={violations}
      onChange={(next) => {
        spy?.(next);
        setValue(next);
      }}
    />
  );
}

describe('ToolPolicyForm', () => {
  it('offers exactly the allowlisted tools and states the rule with the chosen count', async () => {
    render(<Harness />);
    expect(screen.getAllByRole('checkbox').map((c) => c.getAttribute('name') ?? '')).toHaveLength(3);
    expect(screen.getByRole('checkbox', { name: 'evidence.read' })).toBeInTheDocument();
    expect(screen.getByText(/1 to 11 unique tools from the allowlist \(0 chosen\)/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: 'run.summary' }));
    expect(screen.getByRole('checkbox', { name: 'run.summary' })).toBeChecked();
    expect(screen.getByText(/\(1 chosen\)/)).toBeInTheDocument();
  });

  it('reports the chosen tools and removes a tool when unchecked', async () => {
    const spy = vi.fn();
    render(<Harness initial={{ tools: ['evidence.read'] }} spy={spy} />);
    await userEvent.click(screen.getByRole('checkbox', { name: 'match.lookup' }));
    expect(spy).toHaveBeenLastCalledWith({ tools: ['evidence.read', 'match.lookup'] });
    await userEvent.click(screen.getByRole('checkbox', { name: 'evidence.read' }));
    expect(spy).toHaveBeenLastCalledWith({ tools: ['match.lookup'] });
  });

  it('hints at tools outside the baseline of the named agent without blocking them', async () => {
    render(<Harness agent={videoAgent} />);
    expect(screen.getAllByText('Outside the baseline of specialist-video-analysis')).toHaveLength(2);
    await userEvent.click(screen.getByRole('checkbox', { name: 'run.summary' }));
    expect(screen.getByRole('checkbox', { name: 'run.summary' })).toBeChecked();
  });

  it('shows no baseline hint without an agent', () => {
    render(<Harness />);
    expect(screen.queryByText(/Outside the baseline/)).toBeNull();
  });

  it('keeps a tool that is not on the allowlist visible and marked', () => {
    render(<Harness initial={{ tools: ['legacy.tool'] }} />);
    expect(screen.getByRole('checkbox', { name: 'legacy.tool' })).toBeChecked();
    expect(screen.getByText('The tool legacy.tool is not on the tool allowlist.')).toBeInTheDocument();
  });

  it('shows violations at their locations', () => {
    const violations: CatalogViolation[] = [
      { code: 'unknown-tool', pointer: '/tools/0', detail: 'evidence.read', location: 'entry:tools:0' },
      { code: 'schema-violation', pointer: '/tools', detail: 'at least one item', location: 'field:tools' },
      { code: 'schema-violation', pointer: '/other', detail: 'elsewhere', location: 'field:other' },
    ];
    render(<Harness initial={{ tools: ['evidence.read'] }} violations={violations} />);
    expect(screen.getByText('The tool evidence.read is not on the tool allowlist.')).toBeInTheDocument();
    expect(screen.getByText(/at least one item/)).toBeInTheDocument();
    expect(screen.queryByText(/elsewhere/)).toBeNull();
  });

  it('shows the empty text for an empty allowlist', () => {
    render(<Harness allowlist={[]} />);
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    expect(screen.getByText(/No tools are listed/)).toBeInTheDocument();
  });
});
