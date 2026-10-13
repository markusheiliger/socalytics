import { render, screen } from '@testing-library/react';
import App from '../../src/App';
import '../../src/i18n';

describe('App', () => {
  it('renders the application name', () => {
    render(<App />);
    expect(screen.getByRole('heading', { name: 'SocAlytics' })).toBeInTheDocument();
  });
});
