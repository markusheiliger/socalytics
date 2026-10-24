import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { PhilosophyProfileView } from '../../../../src/features/agent-catalog/artifacts/PhilosophyProfileView';
import type { DimensionSetDocument, PhilosophyProfileDocument } from '../../../../src/platform/models/agentCatalog';
import '../../../../src/i18n';
import { dimensionSetRecord, distances, profileRecord } from './fixtures';

const document = profileRecord.document as PhilosophyProfileDocument;
const dimensionSet = dimensionSetRecord.document as DimensionSetDocument;

function renderView(props: Partial<Parameters<typeof PhilosophyProfileView>[0]> = {}) {
  return render(
    <MemoryRouter>
      <PhilosophyProfileView document={document} profileId="possession-first" dimensionSet={dimensionSet} {...props} />
    </MemoryRouter>,
  );
}

describe('PhilosophyProfileView', () => {
  it('shows the rows in the order of the dimension set with meaning, weight, rationale, and provenance', () => {
    renderView();
    const rows = within(screen.getByRole('table')).getAllByRole('row');
    expect(rows).toHaveLength(3);
    expect(within(rows[1]).getByText('pressing')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Intensity of pressing')).toBeInTheDocument();
    expect(within(rows[1]).getByText('40')).toBeInTheDocument();
    expect(within(rows[1]).getByText('High press wins the ball early.')).toBeInTheDocument();
    expect(within(rows[2]).getByText('possession')).toBeInTheDocument();
    expect(within(rows[2]).getByText('60')).toBeInTheDocument();
    expect(screen.getByText('Total').nextSibling).toHaveTextContent('100');
  });

  it('shows the dimension set link, author, reviewers, and no superseded version', () => {
    renderView();
    expect(screen.getByRole('link', { name: 'football-dimensions 1.0.0' })).toHaveAttribute(
      'href',
      '/agent-catalog/artifacts/dimension-sets/football-dimensions/versions/1.0.0',
    );
    expect(screen.getByText('grace', { selector: 'dd' })).toBeInTheDocument();
    expect(screen.getByText('ada')).toBeInTheDocument();
    expect(screen.getByText('Supersedes version').nextSibling).toHaveTextContent('none');
  });

  it('links the superseded version', () => {
    renderView({ document: { ...document, supersedes: { version: '0.9.0' } } });
    expect(screen.getByRole('link', { name: '0.9.0' })).toHaveAttribute(
      'href',
      '/agent-catalog/artifacts/philosophy-profiles/possession-first/versions/0.9.0',
    );
  });

  it('lists the distances through the distance list and marks reviews', () => {
    renderView({ distances });
    expect(screen.getByRole('list', { name: 'Distances to other profiles' })).toBeInTheDocument();
    expect(screen.getByText(/below 0.10/)).toBeInTheDocument();
  });

  it('shows no distances section when none are given', () => {
    renderView();
    expect(screen.queryByText('Distances to other profiles')).toBeNull();
  });

  it('falls back to the stored weights without the dimension set', () => {
    renderView({ dimensionSet: undefined });
    expect(within(screen.getByRole('table')).getAllByRole('row')).toHaveLength(3);
  });
});
