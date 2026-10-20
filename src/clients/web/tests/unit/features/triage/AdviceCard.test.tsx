import { render, screen } from '@testing-library/react';
import { AdviceCard } from '../../../../src/features/triage/AdviceCard';
import type { Advice } from '../../../../src/platform/models/agents';
import '../../../../src/i18n';

const advice = (over: Partial<Advice> = {}): Advice => ({ text: 'Press higher.', evidenceReferences: [], ...over });

describe('AdviceCard', () => {
  it('attributes the advice to its agent and always shows the AI disclosure', () => {
    render(<AdviceCard advice={advice()} agentId="specialist-tactics" />);
    expect(screen.getByRole('heading', { name: 'Advice from Tactics specialist' })).toBeInTheDocument();
    expect(screen.getByText('Tactics specialist (specialist-tactics)')).toBeInTheDocument();
    expect(screen.getByText(/AI-generated advice for a coach's review/)).toBeInTheDocument();
  });

  it('falls back to the identifier for an unknown agent', () => {
    render(<AdviceCard advice={advice()} agentId="specialist-new" />);
    expect(screen.getByText('specialist-new (specialist-new)')).toBeInTheDocument();
  });

  it('states explicitly that there are no evidence references', () => {
    render(<AdviceCard advice={advice()} agentId="specialist-tactics" />);
    expect(screen.getByText('No evidence references were recorded for this advice.')).toBeInTheDocument();
    expect(screen.queryByRole('list')).toBeNull();
  });

  it('lists the evidence references as text', () => {
    render(
      <AdviceCard
        advice={advice({ evidenceReferences: [{ kind: 'fact', reference: 'run/1/<b>x</b>' }, { reference: 'ref-2' }] })}
        agentId="specialist-tactics"
      />,
    );
    expect(screen.queryByText('No evidence references were recorded for this advice.')).toBeNull();
    const items = screen.getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('fact: run/1/<b>x</b>');
    expect(items[1]).toHaveTextContent('ref-2');
    expect(items[0].querySelector('b')).toBeNull();
  });

  it('shows markup-containing advice unaltered as text with preserved line breaks', () => {
    const text = '<script>alert(1)</script>\n**bold** <img src=x onerror=alert(1)>\n\n  indented & [link](http://x)';
    const { container } = render(<AdviceCard advice={advice({ text })} agentId="specialist-tactics" />);
    const paragraph = screen.getByLabelText('Advice');
    expect(paragraph.textContent).toBe(text);
    expect(getComputedStyle(paragraph).whiteSpace).toBe('pre-wrap');
    expect(container.querySelector('script, img, strong, a')).toBeNull();
  });

  it('shows long advice in full', () => {
    const text = `${'A long assessment. '.repeat(500)}${'x'.repeat(2000)}`;
    render(<AdviceCard advice={advice({ text })} agentId="specialist-tactics" />);
    expect(screen.getByLabelText('Advice').textContent).toBe(text);
  });
});
