import { render, screen } from '@testing-library/react';
import { ModifierPolicyView } from '../../../../src/features/agent-catalog/artifacts/ModifierPolicyView';
import '../../../../src/i18n';
import { modifierRecord } from './fixtures';

const document = modifierRecord.document;

const valueOf = (label: string) => screen.getByText(label).nextElementSibling?.textContent;

describe('ModifierPolicyView', () => {
  it('shows every field, the budget-neutral rule, and translated context categories', () => {
    render(<ModifierPolicyView document={document} />);
    expect(screen.getByText('Dimension set major version')).toBeInTheDocument();
    expect(valueOf('Dimension set major version')).toBe('1');
    expect(valueOf('Per-dimension bound')).toBe('2');
    expect(valueOf('Total positive movement')).toBe('5');
    expect(valueOf('Budget-neutral rule')).toBe('Movements must net to zero');
    expect(screen.getByText('Score state')).toBeInTheDocument();
    expect(screen.getByText('Match period')).toBeInTheDocument();
  });

  it('keeps unknown categories raw and shows the empty text', () => {
    const { rerender } = render(<ModifierPolicyView document={{ ...document, contextCategories: ['weather'] }} />);
    expect(screen.getByText('Unknown value: weather')).toBeInTheDocument();
    rerender(<ModifierPolicyView document={{ ...document, contextCategories: [], budgetNeutral: false }} />);
    expect(screen.getByText(/No context categories are chosen/)).toBeInTheDocument();
    expect(valueOf('Budget-neutral rule')).toBe('Movements need not net to zero');
  });
});
