import type { Metadata } from "next";
import { Manrope, Public_Sans } from "next/font/google";
import Providers from "./providers";
import "./globals.css";

const manrope = Manrope({
  subsets: ["latin"],
  weight: ["600", "700"],
  variable: "--font-manrope",
});

const publicSans = Public_Sans({
  subsets: ["latin"],
  weight: ["400", "500", "600"],
  variable: "--font-public-sans",
});

export const metadata: Metadata = {
  title: "Anamnys",
  description: "AI-assisted clinical note drafting",
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body className={`${manrope.variable} ${publicSans.variable} antialiased`}>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
