import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import Container from '@mui/material/Container';
import CssBaseline from '@mui/material/CssBaseline';
import Typography from '@mui/material/Typography';
import { ThemeProvider } from '@mui/material/styles';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import theme from './theme';

export default function App() {
  const [queryClient] = useState(() => new QueryClient());
  const { t } = useTranslation('common');

  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        <Container component="main" maxWidth="md" sx={{ py: 4 }}>
          <Typography variant="h1">{t('app.name')}</Typography>
        </Container>
      </ThemeProvider>
    </QueryClientProvider>
  );
}
