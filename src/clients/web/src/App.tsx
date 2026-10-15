import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import CssBaseline from '@mui/material/CssBaseline';
import { ThemeProvider } from '@mui/material/styles';
import { useState } from 'react';
import { RouterProvider } from 'react-router';
import { ConnectivityProvider } from './connectivity/ConnectivityProvider';
import { createAppRouter } from './router';
import { SessionProvider } from './session/SessionProvider';
import theme from './theme';
import { UploadProvider } from './uploads/UploadProvider';

export default function App() {
  const [queryClient] = useState(() => new QueryClient());
  const [router] = useState(createAppRouter);

  return (
    <QueryClientProvider client={queryClient}>
      <ConnectivityProvider>
        <ThemeProvider theme={theme}>
          <CssBaseline />
          <SessionProvider>
            <UploadProvider>
              <RouterProvider router={router} />
            </UploadProvider>
          </SessionProvider>
        </ThemeProvider>
      </ConnectivityProvider>
    </QueryClientProvider>
  );
}
