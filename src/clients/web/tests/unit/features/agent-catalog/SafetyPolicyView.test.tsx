import { render, screen } from '@testing-library/react';
import { SafetyPolicyView } from '../../../../src/features/agent-catalog/artifacts/SafetyPolicyView';
import '../../../../src/i18n';
import { safetyRecord } from './fixtures';

const document = safetyRecord.document as Parameters<typeof SafetyPolicyView>[0]['document'];

describe('SafetyPolicyView', () => {
  it('shows every field, translating known values and keeping unknown ones raw', () => {
    render(
      <SafetyPolicyView
        document={{ ...document, escalationCategories: ['medical', 'safety'], rules: ['non-fabrication', 'advice-only'] }}
      />,
    );
    expect(screen.getByText('Specialist Agent')).toBeInTheDocument();
    expect(screen.getByText('User to Specialist Agent')).toBeInTheDocument();
    expect(screen.getByText('Coach Agent to Specialist Agent')).toBeInTheDocument();
    expect(screen.getByLabelText('Hard boundaries').textContent).toBe('Never reveal other teams\u2019 data.');
    expect(screen.getByText('Medical')).toBeInTheDocument();
    expect(screen.getByText('Unknown value: safety')).toBeInTheDocument();
    expect(screen.getByText('Unknown value: out-of-scope')).toBeInTheDocument();
    expect(screen.getByText('Non-fabrication')).toBeInTheDocument();
    expect(screen.getByText('Unknown value: advice-only')).toBeInTheDocument();
  });

  it('shows hard boundaries as text, not markup, and the empty texts', () => {
    const { container } = render(
      <SafetyPolicyView document={{ ...document, hardBoundaries: ['<i>x</i>'], routes: [], rules: [], escalationCategories: [], refusalCategories: [] }} />,
    );
    expect(screen.getByLabelText('Hard boundaries').textContent).toBe('<i>x</i>');
    expect(container.querySelector('i')).toBeNull();
    expect(screen.getByText(/No routes are chosen/)).toBeInTheDocument();
    expect(screen.getByText(/No rules are chosen/)).toBeInTheDocument();
    expect(screen.getAllByText(/None are chosen/)).toHaveLength(2);
  });
});
