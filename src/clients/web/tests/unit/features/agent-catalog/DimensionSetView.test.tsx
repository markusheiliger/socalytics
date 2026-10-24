import { render, screen } from '@testing-library/react';
import { DimensionSetView } from '../../../../src/features/agent-catalog/artifacts/DimensionSetView';
import '../../../../src/i18n';
import { dimensionSetRecord } from './fixtures';

const document = dimensionSetRecord.document as Parameters<typeof DimensionSetView>[0]['document'];

describe('DimensionSetView', () => {
  it('shows each dimension with its meaning and accepted evidence tools', () => {
    render(<DimensionSetView document={document} />);
    expect(screen.getByText('pressing')).toBeInTheDocument();
    expect(screen.getByText('Intensity of pressing')).toBeInTheDocument();
    expect(screen.getByText('pressing-events')).toBeInTheDocument();
    expect(screen.getByText('possession')).toBeInTheDocument();
    expect(screen.getByText('Preference for keeping the ball')).toBeInTheDocument();
    expect(screen.getByText('possession-share')).toBeInTheDocument();
  });

  it('shows the empty text for no dimensions', () => {
    render(<DimensionSetView document={{ dimensions: [] }} />);
    expect(screen.getByText(/No dimensions are defined/)).toBeInTheDocument();
  });
});
