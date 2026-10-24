import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { ModifierPolicyForm } from '../../../../src/features/agent-catalog/artifacts/ModifierPolicyForm';
import type { CatalogViolation, ModifierPolicyDocument } from '../../../../src/platform/models/agentCatalog';
import '../../../../src/i18n';

const initialValue: ModifierPolicyDocument = {
  dimensionSetMajor: 1,
  perDimensionBound: 2,
  maxPositiveMovement: 5,
  contextCategories: [],
  budgetNeutral: true,
};

function Harness({
  initial = initialValue,
  violations,
  onChange,
}: {
  initial?: ModifierPolicyDocument;
  violations?: CatalogViolation[];
  onChange?: (value: ModifierPolicyDocument) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <ModifierPolicyForm
      value={value}
      violations={violations}
      onChange={(next) => {
        setValue(next);
        onChange?.(next);
      }}
    />
  );
}

describe('ModifierPolicyForm', () => {
  it('hints the allowed bounds and shows the budget-neutral rule', () => {
    render(<Harness />);
    expect(screen.getByText('Whole numbers from 1 to 3')).toBeInTheDocument();
    expect(screen.getByText('Whole numbers from 1 to 10')).toBeInTheDocument();
    expect(screen.getByText('Movements must net to zero')).toBeInTheDocument();
    expect(screen.getByText(/must be budget-neutral/)).toBeInTheDocument();
    expect(screen.getAllByRole('checkbox')).toHaveLength(5);
    expect(screen.getByText(/No context categories are chosen/)).toBeInTheDocument();
  });

  it('reports numeric and category changes, and does not block a bound outside the range', () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    fireEvent.change(screen.getByLabelText(/Per-dimension bound/), { target: { value: '4' } });
    expect(onChange).toHaveBeenLastCalledWith({ ...initialValue, perDimensionBound: 4 });
    fireEvent.change(screen.getByLabelText(/Total positive movement/), { target: { value: '7' } });
    expect(onChange).toHaveBeenLastCalledWith({ ...initialValue, perDimensionBound: 4, maxPositiveMovement: 7 });
    fireEvent.click(screen.getByLabelText('Score state'));
    expect(onChange).toHaveBeenLastCalledWith({
      ...initialValue,
      perDimensionBound: 4,
      maxPositiveMovement: 7,
      contextCategories: ['score-state'],
    });
    fireEvent.click(screen.getByLabelText('Score state'));
    expect(onChange).toHaveBeenLastCalledWith({ ...initialValue, perDimensionBound: 4, maxPositiveMovement: 7 });
  });

  it('shows violations next to their fields and keeps the entered values', () => {
    const violations: CatalogViolation[] = [
      { code: 'modifier-bound-exceeded', pointer: '/perDimensionBound', detail: '', location: 'field:perDimensionBound' },
      { code: 'context-category-not-permitted', pointer: '/contextCategories/0', detail: '', location: 'entry:contextCategories:0' },
    ];
    render(<Harness initial={{ ...initialValue, perDimensionBound: 4, contextCategories: ['weather'] }} violations={violations} />);
    expect(screen.getByText(/exceeds the allowed bounds/)).toBeInTheDocument();
    expect(screen.getByText('This context category is not permitted.')).toBeInTheDocument();
    expect(screen.getByLabelText(/Per-dimension bound/)).toHaveValue(4);
    expect(screen.getByText('Unknown value: weather')).toBeInTheDocument();
  });
});
