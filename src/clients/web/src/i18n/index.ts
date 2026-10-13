import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import common from './en/common.json';

void i18n.use(initReactI18next).init({
  lng: 'en',
  fallbackLng: 'en',
  defaultNS: 'common',
  ns: ['common'],
  resources: { en: { common } },
  interpolation: { escapeValue: false },
});

export default i18n;
