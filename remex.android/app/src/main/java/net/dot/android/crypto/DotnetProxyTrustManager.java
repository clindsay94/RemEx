package net.dot.android.crypto;

import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;

import javax.net.ssl.X509TrustManager;

public final class DotnetProxyTrustManager implements X509TrustManager {
    private final long sslStreamProxyHandle;

    public DotnetProxyTrustManager(long sslStreamProxyHandle) {
        this.sslStreamProxyHandle = sslStreamProxyHandle;
    }

    @Override
    public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
        if (!verifyRemoteCertificate(sslStreamProxyHandle)) {
            throw new CertificateException();
        }
    }

    @Override
    public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
        if (!verifyRemoteCertificate(sslStreamProxyHandle)) {
            throw new CertificateException();
        }
    }

    @Override
    public X509Certificate[] getAcceptedIssuers() {
        return new X509Certificate[0];
    }

    static native boolean verifyRemoteCertificate(long sslStreamProxyHandle);
}
