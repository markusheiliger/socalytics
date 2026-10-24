import { render, screen } from '@testing-library/react';
import { ModelSelectionView } from '../../../../src/features/agent-catalog/artifacts/ModelSelectionView';
import '../../../../src/i18n';
import { modelRecord } from './fixtures';

const document = modelRecord.document as Parameters<typeof ModelSelectionView>[0]['document'];

describe('ModelSelectionView', () => {
  it('shows provider, model, deployment, and every parameter', () => {
    render(<ModelSelectionView document={document} />);
    expect(screen.getByText('Simulated (Development and Test only)')).toBeInTheDocument();
    expect(screen.getByText('simulated-1')).toBeInTheDocument();
    expect(screen.getByText('development')).toBeInTheDocument();
    expect(screen.getByText('0.2')).toBeInTheDocument();
    expect(screen.getByText('1024')).toBeInTheDocument();
    expect(screen.getByText('Low')).toBeInTheDocument();
  });

  it('omits parameters that are not set and shows unknown values raw', () => {
    render(<ModelSelectionView document={{ ...document, provider: 'other' as never, parameters: { reasoningEffort: 'extreme' as never } }} />);
    expect(screen.getByText('Unknown value: other')).toBeInTheDocument();
    expect(screen.getByText('Unknown value: extreme')).toBeInTheDocument();
    expect(screen.queryByText('Temperature')).toBeNull();
    expect(screen.queryByText('Maximum output tokens')).toBeNull();
  });
});
