import { render, screen } from '@testing-library/react';
import { ToolPolicyView } from '../../../../src/features/agent-catalog/artifacts/ToolPolicyView';
import '../../../../src/i18n';
import { toolRecord, videoAgent } from './fixtures';

const document = toolRecord.document as Parameters<typeof ToolPolicyView>[0]['document'];

describe('ToolPolicyView', () => {
  it('lists the tools and marks each within or outside the baseline of the agent', () => {
    render(<ToolPolicyView document={document} agent={videoAgent} />);
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByText('evidence.read (within the baseline)')).toBeInTheDocument();
    expect(screen.getByText('run.summary (outside the baseline)')).toBeInTheDocument();
  });

  it('shows no marker without an agent in context', () => {
    render(<ToolPolicyView document={document} />);
    expect(screen.getByText('evidence.read')).toBeInTheDocument();
    expect(screen.queryByText(/baseline/)).toBeNull();
  });

  it('shows the empty text for no tools', () => {
    render(<ToolPolicyView document={{ tools: [] }} />);
    expect(screen.getByText(/No tools are listed/)).toBeInTheDocument();
  });
});
