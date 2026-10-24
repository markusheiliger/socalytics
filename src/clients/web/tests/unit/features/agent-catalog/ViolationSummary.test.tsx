import { render, screen } from '@testing-library/react';
import { ViolationSummary } from '../../../../src/features/agent-catalog/ViolationSummary';
import type { CatalogViolation } from '../../../../src/platform/models/agentCatalog';
import '../../../../src/i18n';

const violation = (code: string, location: string, detail = '', pointer = ''): CatalogViolation => ({ code, pointer, detail, location });

describe('ViolationSummary', () => {
  it('announces only violations without a known location, in words', () => {
    render(
      <ViolationSummary
        violations={[
          violation('unknown-tool', 'summary', 'web-search', '/tools/0/'),
          violation('weights-total-not-100', 'weights'),
          violation('brand-new-code', 'summary', 'some detail'),
        ]}
      />,
    );
    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('Problems without a field');
    expect(alert).toHaveTextContent('The tool web-search is not on the tool allowlist.');
    expect(alert).toHaveTextContent('The platform reported brand-new-code: some detail');
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
  });

  it('renders nothing when every violation has a location', () => {
    const { container } = render(<ViolationSummary violations={[violation('weights-total-not-100', 'weights')]} />);
    expect(container).toBeEmptyDOMElement();
  });
});
