import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import CssBaseline from '@mui/material/CssBaseline';
import { ThemeProvider } from '@mui/material/styles';
import { useState } from 'react';
import { RouterProvider } from 'react-router';
import { ConnectivityProvider } from './connectivity/ConnectivityProvider';
import { createAppRouter } from './router';
import { SessionProvider } from './session/SessionProvider';
import theme from './theme';

export default function App() {
  const [queryClient] = useState(() => new QueryClient());
  const [router] = useState(createAppRouter);

  return (
    <QueryClientProvider client={queryClient}>
      <ConnectivityProvider>
        <ThemeProvider theme={theme}>
          <CssBaseline />
          <SessionProvider>
            <RouterProvider router={router} />
          </SessionProvider>
        </ThemeProvider>
      </ConnectivityProvider>
    </QueryClientProvider>
  );
}
