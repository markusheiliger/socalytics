import { render, screen } from '@testing-library/react';
import { CatalogText } from '../../../../src/features/agent-catalog/CatalogText';

describe('CatalogText', () => {
  it('shows markup and placeholders verbatim as text', () => {
    const text = 'Line one\n  <b>bold</b> <script>alert(1)</script>\n{{coach.name}} {{slot}}';
    const { container } = render(<CatalogText text={text} label="Prompt text" />);
    const block = screen.getByLabelText('Prompt text');
    expect(block.tagName).toBe('PRE');
    expect(block.textContent).toBe(text);
    expect(container.querySelector('b, script')).toBeNull();
  });

  it('is focusable so it can be scrolled by keyboard', () => {
    render(<CatalogText text="x" label="Prompt text" />);
    expect(screen.getByLabelText('Prompt text')).toHaveAttribute('tabindex', '0');
  });
});
