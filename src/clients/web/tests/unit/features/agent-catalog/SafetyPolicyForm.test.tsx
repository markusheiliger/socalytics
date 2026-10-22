import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { SafetyPolicyForm } from '../../../../src/features/agent-catalog/artifacts/SafetyPolicyForm';
import type { CatalogViolation, SafetyPolicyDocument } from '../../../../src/platform/models/agentCatalog';
import '../../../../src/i18n';

const empty: SafetyPolicyDocument = {
  group: 'specialist',
  routes: [],
  hardBoundaries: [],
  escalationCategories: [],
  refusalCategories: [],
  rules: [],
};

function Harness({
  initial = empty,
  violations,
  onChange,
}: {
  initial?: SafetyPolicyDocument;
  violations?: CatalogViolation[];
  onChange?: (value: SafetyPolicyDocument) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <SafetyPolicyForm
      value={value}
      violations={violations}
      onChange={(next) => {
        setValue(next);
        onChange?.(next);
      }}
    />
  );
}

describe('SafetyPolicyForm', () => {
  it('offers only the allowed choices and reports changes', () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    expect(screen.getAllByRole('checkbox')).toHaveLength(3 + 8 + 8 + 6);
    expect(screen.getByText(/No routes are chosen/)).toBeInTheDocument();
    expect(screen.getByText(/No hard boundaries are listed/)).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText('User to Specialist Agent'));
    expect(onChange).toHaveBeenLastCalledWith({ ...empty, routes: ['user-to-specialist'] });
    fireEvent.click(screen.getByLabelText('User to Specialist Agent'));
    expect(onChange).toHaveBeenLastCalledWith(empty);
  });

  it('adds, edits, and removes hard boundaries with the remaining length', () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: 'Add hard boundary' }));
    expect(screen.getByText('500 characters remaining')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Hard boundary 1'), { target: { value: 'Never guess.' } });
    expect(onChange).toHaveBeenLastCalledWith({ ...empty, hardBoundaries: ['Never guess.'] });
    expect(screen.getByText('488 characters remaining')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Remove hard boundary 1' }));
    expect(onChange).toHaveBeenLastCalledWith(empty);
  });

  it('hints the Coach Agent rules only for coach, and does not block', () => {
    const { unmount } = render(<Harness initial={{ ...empty, group: 'coach', rules: ['non-fabrication'] }} />);
    expect(screen.getByText(/Not chosen yet: Simulation disclosure, Non-impersonation/)).toBeInTheDocument();
    unmount();
    render(<Harness />);
    expect(screen.queryByText(/Coach Agent safety policy is expected/)).toBeNull();
  });

  it('switches the group', () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    fireEvent.mouseDown(screen.getByRole('combobox', { name: /Group/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Coach Agent' }));
    expect(onChange).toHaveBeenLastCalledWith({ ...empty, group: 'coach' });
  });

  it('shows violations at their locations and keeps the entered values', () => {
    const violations: CatalogViolation[] = [
      { code: 'safety-rule-missing', pointer: '/rules', detail: '', location: 'field:rules' },
      { code: 'schema-violation', pointer: '/hardBoundaries/0', detail: 'too long', location: 'entry:hardBoundaries:0' },
      { code: 'schema-violation', pointer: '/routes/0', detail: 'bad route', location: 'entry:routes:0' },
    ];
    render(<Harness initial={{ ...empty, routes: ['x-route'], hardBoundaries: ['Keep me'] }} violations={violations} />);
    expect(screen.getByText('The safety policy lacks a rule that this group requires.')).toBeInTheDocument();
    expect(screen.getByText(/too long/)).toBeInTheDocument();
    expect(screen.getByText(/bad route/)).toBeInTheDocument();
    expect(screen.getByLabelText('Hard boundary 1')).toHaveValue('Keep me');
    expect(screen.getByText('Unknown value: x-route')).toBeInTheDocument();
  });
});
