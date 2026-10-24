import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import {
  PhilosophyProfileForm,
  type DimensionSetChoice,
} from '../../../../src/features/agent-catalog/artifacts/PhilosophyProfileForm';
import type { CatalogViolation, DimensionSetDocument, PhilosophyProfileDocument } from '../../../../src/platform/models/agentCatalog';
import '../../../../src/i18n';
import { dimensionSetRecord } from './fixtures';

const setOne: DimensionSetChoice = {
  id: 'football-dimensions',
  version: '1.0.0',
  document: dimensionSetRecord.document as DimensionSetDocument,
};
const setTwo: DimensionSetChoice = {
  id: 'football-dimensions',
  version: '1.1.0',
  document: {
    dimensions: [
      { id: 'pressing', meaning: 'Intensity of pressing', acceptedEvidence: ['pressing-events'] },
      { id: 'width', meaning: 'Use of width', acceptedEvidence: ['width-events'] },
    ],
  },
};

const empty: PhilosophyProfileDocument = {
  dimensionSet: { id: '', version: '' },
  weights: {},
  author: '',
  reviewers: { architecture: [], footballDomain: [] },
};

function Harness({
  initial = empty,
  violations,
  spy,
}: {
  initial?: PhilosophyProfileDocument;
  violations?: CatalogViolation[];
  spy?: (value: PhilosophyProfileDocument) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <PhilosophyProfileForm
      value={value}
      dimensionSets={[setOne, setTwo]}
      violations={violations}
      onChange={(next) => {
        spy?.(next);
        setValue(next);
      }}
    />
  );
}

async function choose(name: string) {
  await userEvent.click(screen.getByRole('combobox', { name: /Dimension set version/ }));
  await userEvent.click(screen.getByRole('option', { name }));
}

describe('PhilosophyProfileForm', () => {
  it('shows the empty text until a dimension set is chosen, then one row per dimension', async () => {
    const spy = vi.fn();
    render(<Harness spy={spy} />);
    expect(screen.getByText(/No rows exist yet/)).toBeInTheDocument();
    await choose('football-dimensions 1.0.0');
    expect(screen.getByLabelText(/Weight of pressing/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Weight of possession/)).toBeInTheDocument();
    expect(spy).toHaveBeenLastCalledWith(expect.objectContaining({ dimensionSet: { id: 'football-dimensions', version: '1.0.0' } }));
  });

  it('hints at a weight outside 0 to 100 and a total that is not 100 without blocking', async () => {
    render(<Harness />);
    await choose('football-dimensions 1.0.0');
    await userEvent.type(screen.getByLabelText(/Weight of pressing/), '101');
    await userEvent.type(screen.getByLabelText(/Weight of possession/), '1');
    expect(screen.getByLabelText(/Weight of pressing/)).toBeInvalid();
    expect(screen.getByLabelText(/Weight of possession/)).toBeValid();
    expect(screen.getByText('Total of the weights: 102 of 100')).toBeInTheDocument();
    expect(screen.getByText('The weights must total 100')).toBeInTheDocument();
  });

  it('shows no total hint when the weights total 100', async () => {
    render(<Harness />);
    await choose('football-dimensions 1.0.0');
    await userEvent.type(screen.getByLabelText(/Weight of pressing/), '40');
    await userEvent.type(screen.getByLabelText(/Weight of possession/), '60');
    expect(screen.getByText('Total of the weights: 100 of 100')).toBeInTheDocument();
    expect(screen.queryByText('The weights must total 100')).toBeNull();
  });

  it('keeps values of matching ids and drops the others with a notice when the dimension set changes', async () => {
    const spy = vi.fn();
    render(<Harness spy={spy} />);
    await choose('football-dimensions 1.0.0');
    await userEvent.type(screen.getByLabelText(/Weight of pressing/), '40');
    await userEvent.type(screen.getByLabelText(/Weight of possession/), '60');
    await choose('football-dimensions 1.1.0');
    expect(screen.getByLabelText(/Weight of pressing/)).toHaveValue(40);
    expect(screen.getByLabelText(/Weight of width/)).toHaveValue(null);
    expect(screen.queryByLabelText(/Weight of possession/)).toBeNull();
    expect(screen.getByText(/were dropped: possession/)).toBeInTheDocument();
    expect(Object.keys(spy.mock.lastCall![0].weights)).toEqual(['pressing', 'width']);
  });

  it('reports rationale, provenance, author, reviewers, and the superseded version', async () => {
    const spy = vi.fn();
    render(<Harness spy={spy} />);
    await choose('football-dimensions 1.0.0');
    await userEvent.type(screen.getByLabelText(/Rationale of pressing/), 'r');
    await userEvent.type(screen.getByLabelText(/Provenance of pressing/), 'p');
    expect(spy.mock.lastCall![0].weights.pressing).toMatchObject({ rationale: 'r', provenance: 'p' });
    await userEvent.type(screen.getByLabelText(/^Author/), 'a');
    await userEvent.click(screen.getByRole('button', { name: 'Add architecture reviewer' }));
    await userEvent.type(screen.getByLabelText(/^Architecture reviewer 1/), 'x');
    await userEvent.click(screen.getByRole('button', { name: 'Add football domain reviewer' }));
    await userEvent.type(screen.getByLabelText(/^Football domain reviewer 1/), 'y');
    await userEvent.type(screen.getByLabelText(/^Supersedes version/), '0.9.0');
    expect(spy.mock.lastCall![0]).toMatchObject({
      author: 'a',
      reviewers: { architecture: ['x'], footballDomain: ['y'] },
      supersedes: { version: '0.9.0' },
    });
    await userEvent.click(screen.getByRole('button', { name: 'Remove architecture reviewer 1' }));
    expect(spy.mock.lastCall![0].reviewers.architecture).toEqual([]);
  });

  it('shows violations at their rows and at the total', async () => {
    const violations: CatalogViolation[] = [
      { code: 'weight-out-of-range', detail: '', pointer: '', location: 'cell:pressing:weight' },
      { code: 'rationale-missing', detail: '', pointer: '', location: 'cell:possession:rationale' },
      { code: 'weights-total-not-100', detail: '', pointer: '', location: 'weights' },
    ];
    render(
      <Harness
        initial={{ ...empty, dimensionSet: { id: setOne.id, version: setOne.version }, weights: {} }}
        violations={violations}
      />,
    );
    await choose('football-dimensions 1.0.0');
    const alerts = screen.getAllByRole('alert').map((a) => a.textContent);
    expect(alerts).toEqual([
      'A weight must be a whole number from 0 to 100.',
      'Every weight needs a rationale and a provenance statement.',
      'The weights must total 100.',
    ]);
  });
});
