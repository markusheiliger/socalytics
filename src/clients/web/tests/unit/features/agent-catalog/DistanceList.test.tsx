import { render, screen } from '@testing-library/react';
import { DistanceList } from '../../../../src/features/agent-catalog/artifacts/DistanceList';
import { distanceWithReview, distanceWithoutReview } from './fixtures';
import '../../../../src/i18n';

describe('DistanceList', () => {
  it('marks every distance that requires a review', () => {
    render(<DistanceList distances={[distanceWithoutReview, distanceWithReview]} />);
    const items = screen.getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('coach-lead 0.1.0 with profile possession-first 1.0.0: 42');
    expect(items[0]).not.toHaveTextContent('differentiation review required');
    expect(items[1]).toHaveTextContent('coach-lead 0.2.0 with profile possession-first 1.0.0: 8');
    expect(items[1]).toHaveTextContent('below 0.10: differentiation review required');
  });

  it('says that no distance applies when empty', () => {
    render(<DistanceList distances={[]} />);
    expect(screen.getByText('The platform reports no distances.')).toBeInTheDocument();
    expect(screen.queryByRole('list')).not.toBeInTheDocument();
  });
});
