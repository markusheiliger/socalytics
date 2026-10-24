import { render, screen } from '@testing-library/react';
import { PromptTemplateView } from '../../../../src/features/agent-catalog/artifacts/PromptTemplateView';
import '../../../../src/i18n';
import { promptRecord } from './fixtures';

const document = promptRecord.document as Parameters<typeof PromptTemplateView>[0]['document'];

describe('PromptTemplateView', () => {
  it('shows every field and the text verbatim without interpreting markup', () => {
    const { container } = render(<PromptTemplateView document={{ ...document, dimensionSetMajor: 1 }} />);
    expect(screen.getByText('Specialist Agent')).toBeInTheDocument();
    expect(screen.getByText(/five sections of the advice governance/)).toBeInTheDocument();
    expect(screen.getByText('Display name')).toBeInTheDocument();
    expect(screen.getByText('Evidence context')).toBeInTheDocument();
    expect(screen.getByText('Dimension set major version')).toBeInTheDocument();
    const text = screen.getByLabelText('Prompt text');
    expect(text.textContent).toBe(document.text);
    expect(container.querySelector('b')).toBeNull();
  });

  it('shows unknown values raw, no major version without one, and the empty slot text', () => {
    render(<PromptTemplateView document={{ ...document, group: 'referee' as never, responseContract: 'agent-response/9', slots: ['mystery'] }} />);
    expect(screen.getByText('Unknown value: referee')).toBeInTheDocument();
    expect(screen.getByText('Unknown value: agent-response/9')).toBeInTheDocument();
    expect(screen.getByText('Unknown value: mystery')).toBeInTheDocument();
    expect(screen.queryByText('Dimension set major version')).toBeNull();
  });

  it('shows the empty text for no slots', () => {
    render(<PromptTemplateView document={{ ...document, slots: [] }} />);
    expect(screen.getByText(/No slots are declared/)).toBeInTheDocument();
  });
});
